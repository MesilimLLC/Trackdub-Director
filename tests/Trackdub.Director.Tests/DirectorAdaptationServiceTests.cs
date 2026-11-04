using System.Net;
using System.Text;
using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Deterministic fake-handler tests for Director Mode Stage B2 register
/// adaptation. The stub dispatches on the template markers: adaptation calls
/// carry DIRECTOR_REGISTER_ADAPTATION, meaning-gate calls carry
/// MEANING_FIDELITY_JUDGE. Call counts are asserted in every test so a code
/// path that silently spends (or skips) a call fails here.
/// </summary>
public sealed class DirectorAdaptationServiceTests
{
    private const string SourceText = "We need one more take.";
    private const string ExistingTranslation = "Necesitamos una toma más.";
    private const string AdaptedInformal = "Claro, una toma más.";
    private const string AdaptedLong = "Necesitaríamos, por favor, realizar una toma adicional completa.";

    private sealed class LaneStub
    {
        public int AdaptationCalls;
        public int MeaningCalls;
        public List<string> AdaptationBodies = [];
        public string AdaptationContent = AdaptedInformal;
        public string AdaptationFinishReason = "stop";
        public string MeaningContent = """{"verdict":"pass","issues":[],"explanation":"Meaning preserved."}""";
        public bool MeaningTransportsError;
        public bool AdaptationTransportsError;
        public bool AdaptationBodyReadFails;

        public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (body.Contains(DirectorAdaptationService.DirectorMarker, StringComparison.Ordinal))
            {
                AdaptationBodies.Add(body);
                return AdaptationResponse();
            }

            if (body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal))
            {
                return MeaningResponse();
            }

            throw new InvalidOperationException($"Unexpected request body: {body}");
        }

        private HttpResponseMessage AdaptationResponse()
        {
            AdaptationCalls++;
            if (AdaptationBodyReadFails)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new BodyReadFailsContent() };
            }

            if (AdaptationTransportsError)
            {
                return UpstreamError();
            }

            return ChatOk(new ChatReply(AdaptationContent, 120, 40, 12, AdaptationFinishReason));
        }

        private HttpResponseMessage MeaningResponse()
        {
            MeaningCalls++;
            if (MeaningTransportsError)
            {
                return UpstreamError();
            }

            return ChatOk(new ChatReply(MeaningContent, 90, 30, null, "stop"));
        }

        private static HttpResponseMessage UpstreamError() => new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"error":{"message":"upstream down"}}""", Encoding.UTF8, "application/json"),
        };
    }

    private sealed record ChatReply(string Content, int PromptTokens, int CompletionTokens, int? ReasoningTokens, string FinishReason);

    private sealed class BodyReadFailsContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new HttpRequestException("body read reset");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static HttpResponseMessage ChatOk(ChatReply reply)
    {
        (string content, int promptTokens, int completionTokens, int? reasoningTokens, string finishReason) = reply;
        var payload = new
        {
            id = "chatcmpl-test-director",
            @object = "chat.completion",
            created = 1793261234L,
            model = "muse-spark-1.1",
            choices = new[] { new { index = 0, finish_reason = finishReason, message = new { role = "assistant", content, refusal = (string?)null } } },
            usage = new
            {
                prompt_tokens = promptTokens,
                completion_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens,
                completion_tokens_details = reasoningTokens.HasValue ? new { reasoning_tokens = reasoningTokens.Value } : null,
                prompt_tokens_details = new { cached_tokens = 0 },
            },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly LaneStub _stub;
        public DelegatingHandlerStub(LaneStub stub) => _stub = stub;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _stub.HandleAsync(request, cancellationToken);
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public EnvScope(IReadOnlyDictionary<string, string> values)
        {
            foreach (KeyValuePair<string, string> entry in values)
            {
                _previous[entry.Key] = Environment.GetEnvironmentVariable(entry.Key);
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, string?> entry in _previous)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }
    }

    private sealed class Composite : IDisposable
    {
        private readonly IDisposable[] _items;
        public Composite(params IDisposable[] items) => _items = items;
        public void Dispose() { foreach (IDisposable d in _items) d.Dispose(); }
    }

    private static IDisposable FakeAuth()
    {
        return new EnvScope(new Dictionary<string, string>
        {
            ["META_API_KEY"] = "test-fake-key",
            ["META_MODEL_API_BASE_URL"] = "https://api.meta.ai/v1",
        });
    }

    private static (DirectorAdaptationService Service, IDisposable Scope) BuildService(LaneStub stub)
    {
        var httpClient = new HttpClient(new DelegatingHandlerStub(stub));
        var metaClient = new MetaClient(httpClient);
        return (new DirectorAdaptationService(metaClient), new Composite(FakeAuth(), httpClient, metaClient));
    }

    private static DirectorAdaptationSegmentInput OptedIn(
        string register = "informal",
        double durationSeconds = 4.0,
        DirectorContext? context = null)
    {
        return new DirectorAdaptationSegmentInput(
            "seg-002", SourceText, ExistingTranslation, durationSeconds, context,
            new DirectorAdaptationSpec(register));
    }

    private static void ExpectMeaning(DirectorAdaptationSegmentResult result, MeaningVerdict verdict)
    {
        // Pass and Fail spent a meaning call (90 + 30 tokens); NotEvaluated and Error spent none.
        int expectedTokens = verdict is MeaningVerdict.Pass or MeaningVerdict.Fail ? 120 : 0;
        Assert.NotNull(result.Meaning);
        Assert.Equal(verdict, result.Meaning.Verdict);
        Assert.Equal(expectedTokens, result.Meaning.Usage.TotalTokens);
    }

    private static void ExpectBilledAdaptationFailure(DirectorAdaptationSegmentResult result, string errorFragment)
    {
        ExpectFailedBeforeGates(result);
        Assert.NotNull(result.Error);
        Assert.Contains(errorFragment, result.Error, StringComparison.Ordinal);

        // The call was made and billed; usage is recorded, not discarded.
        ExpectBilledUsage(result);
    }

    private static void ExpectPassed(DirectorAdaptationSegmentResult result, string adapted)
    {
        Assert.Equal(DirectorAdaptationStatus.Passed, result.Status);
        Assert.Equal(adapted, result.AdaptedTranslation);
    }

    private static void ExpectBothGatesRan(DirectorAdaptationSegmentResult result, LaneStub stub)
    {
        Assert.Equal(QaVerdict.Pass, result.TimingVerdict);
        ExpectMeaning(result, MeaningVerdict.Pass);
        Assert.Equal((1, 1), (stub.AdaptationCalls, stub.MeaningCalls));
    }

    private static void ExpectFailedBeforeGates(DirectorAdaptationSegmentResult result)
    {
        Assert.Equal(DirectorAdaptationStatus.Failed, result.Status);
        Assert.Null(result.AdaptedTranslation);
        Assert.Null(result.TimingVerdict);

        var meaning = result.Meaning;
        Assert.Null(meaning);
    }

    private static void ExpectBilledUsage(DirectorAdaptationSegmentResult result)
    {
        Assert.NotNull(result.Usage);
        Assert.Equal(160, result.Usage.TotalTokens);
    }

    private static void ExpectProvenance(DirectorAdaptationSegmentResult result)
    {
        var provenance = result.Provenance;
        Assert.NotNull(provenance);
        Assert.Equal("muse-spark-1.1", provenance.ModelId);
        Assert.Equal("api.meta.ai", provenance.EndpointHost);

        string template = provenance.PromptTemplateVersion;
        Assert.Equal("director-v1", template);
    }

    private static void ExpectNoSpend(DirectorAdaptationSegmentResult result)
    {
        Assert.Null(result.AdaptedTranslation);
        Assert.Null(result.Usage);
    }

    private static void ExpectBodyContains(string body, params string[] fragments)
    {
        foreach (string fragment in fragments)
        {
            Assert.Contains(fragment, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AdaptationProduced_AndBothGatesPass()
    {
        var stub = new LaneStub();
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        DirectorAdaptationBatch batch = await service.RunAsync([OptedIn()], "en", "es");

        DirectorAdaptationSegmentResult result = batch.Segments.Single();
        Assert.Equal(DirectorAdaptationStatus.Passed, result.Status);
        Assert.Equal(ExistingTranslation, result.OriginalTranslation);
        Assert.Equal(AdaptedInformal, result.AdaptedTranslation);

        string? register = result.TargetRegister;
        Assert.Equal("informal", register);

        ExpectMeaning(result, MeaningVerdict.Pass);
        Assert.Null(result.Error);
        Assert.Empty(result.Meaning!.Issues);

        double? estimated = result.EstimatedSeconds;
        Assert.Equal(QaVerdict.Pass, result.TimingVerdict);
        Assert.NotNull(estimated);
        Assert.Equal(4.0, result.WindowSeconds);

        ExpectProvenance(result);
        ExpectBilledUsage(result);

        int? reasoning = result.Usage?.ReasoningTokens;
        Assert.Equal(12, reasoning);

        Assert.Equal((1, 1), (stub.AdaptationCalls, stub.MeaningCalls));
        Assert.Equal(160, batch.UsageTotal.TotalTokens);
        Assert.Equal(120, batch.MeaningUsageTotal.TotalTokens);

        string body = stub.AdaptationBodies.Single();
        // Body is JSON-escaped (á becomes á), so match the ASCII prefix.
        ExpectBodyContains(body, "muse-spark-1.1", "\"reasoning_effort\":\"low\"", SourceText, "Necesitamos una toma", "informal");
    }

    [Fact]
    public async Task AdaptedLineFailsMeaning_ReportedFailedWithIssuesPreserved()
    {
        var stub = new LaneStub
        {
            MeaningContent = """{"verdict":"fail","issues":["dropped modality: source says the take is needed"],"explanation":"Candidate drops the obligation."}""",
        };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        DirectorAdaptationBatch batch = await service.RunAsync([OptedIn()], "en", "es");

        Assert.Equal((1, 1), (stub.AdaptationCalls, stub.MeaningCalls));
        DirectorAdaptationSegmentResult result = batch.Segments.Single();
        Assert.Equal(DirectorAdaptationStatus.Failed, result.Status);
        Assert.Equal(AdaptedInformal, result.AdaptedTranslation);
        Assert.Equal(QaVerdict.Pass, result.TimingVerdict);

        var meaning = result.Meaning;
        ExpectMeaning(result, MeaningVerdict.Fail);
        Assert.Equal(["dropped modality: source says the take is needed"], meaning!.Issues);
        Assert.Equal("Candidate drops the obligation.", meaning.Explanation);
    }

    [Fact]
    public async Task AdaptedLineFailsTiming_MeaningNotEvaluatedWithZeroMeaningSpend()
    {
        var stub = new LaneStub { AdaptationContent = AdaptedLong };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        DirectorAdaptationBatch batch = await service.RunAsync([OptedIn("formal", durationSeconds: 0.5)], "en", "es");

        DirectorAdaptationSegmentResult result = batch.Segments.Single();
        Assert.Equal(DirectorAdaptationStatus.Failed, result.Status);
        Assert.Equal(AdaptedLong, result.AdaptedTranslation);
        Assert.Equal(QaVerdict.Fail, result.TimingVerdict);

        double? estimated = result.EstimatedSeconds;
        ExpectMeaning(result, MeaningVerdict.NotEvaluated);
        Assert.NotNull(estimated);
        Assert.True(estimated.Value > 0.5);

        double? window = result.WindowSeconds;
        Assert.Equal(0.5, window);
        Assert.Equal((1, 0), (stub.AdaptationCalls, stub.MeaningCalls));
        Assert.Equal(0, batch.MeaningUsageTotal.TotalTokens);
    }

    [Fact]
    public async Task ContextPresent_FieldsUsedReflectsOnlyNonNullFields_ContextAbsent_IsEmpty()
    {
        var stub = new LaneStub();
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        // Deserialized from the wire shape the validation input uses: context
        // inline with two fields set, one blank, one missing.
        const string json = """
            [
              {
                "id": "seg-002",
                "text": "We need one more take.",
                "translation": "Necesitamos una toma más.",
                "duration_seconds": 4.0,
                "context": {
                  "emotion_tone_label": "cheerful",
                  "scene_summary": "A woman stands outside a coffee shop entrance talking on her cell phone.",
                  "speaker_description": "",
                  "on_screen_text": []
                },
                "director_adaptation": { "target_register": "informal" }
              },
              {
                "id": "seg-003",
                "text": "Action!",
                "translation": "¡Acción!",
                "duration_seconds": 4.0,
                "director_adaptation": { "target_register": "formal" }
              }
            ]
            """;
        string tmp = Path.Combine(Path.GetTempPath(), $"director-b2-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tmp, json);
        try
        {
            IReadOnlyList<DirectorAdaptationSegmentInput> segments =
                await DirectorAdaptationService.LoadSegmentsAsync(tmp);
            DirectorAdaptationBatch batch = await service.RunAsync(segments, "en", "es");

            DirectorAdaptationSegmentResult withContext = batch.Segments[0];
            Assert.Equal(["emotion_tone_label", "scene_summary"], withContext.ContextFieldsUsed);
            Assert.Equal(DirectorAdaptationStatus.Passed, withContext.Status);
            ExpectBodyContains(stub.AdaptationBodies[0], "cheerful", "coffee shop");

            DirectorAdaptationSegmentResult withoutContext = batch.Segments[1];
            Assert.Empty(withoutContext.ContextFieldsUsed);
            Assert.Equal(DirectorAdaptationStatus.Passed, withoutContext.Status);
            Assert.DoesNotContain("SCENE CONTEXT", stub.AdaptationBodies[1], StringComparison.Ordinal);

            Assert.Equal((2, 2), (stub.AdaptationCalls, stub.MeaningCalls));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task InvalidRegister_FailsClosedWithZeroSpend_AndUntouchedSegmentMakesNoCalls()
    {
        var stub = new LaneStub();
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        var segments = new[]
        {
            OptedIn("royal"),
            new DirectorAdaptationSegmentInput("seg-009", "Quiet on set, please.", "Silencio en el set, por favor.", 1.0),
        };

        DirectorAdaptationBatch batch = await service.RunAsync(segments, "en", "es");

        DirectorAdaptationSegmentResult invalid = batch.Segments[0];
        Assert.Equal(DirectorAdaptationStatus.Failed, invalid.Status);
        Assert.Equal("royal", invalid.TargetRegister);
        ExpectNoSpend(invalid);
        Assert.Null(invalid.Provenance);
        Assert.NotNull(invalid.Error);
        Assert.Contains("target_register", invalid.Error, StringComparison.Ordinal);

        DirectorAdaptationSegmentResult untouched = batch.Segments[1];
        Assert.Equal(DirectorAdaptationStatus.NotRequested, untouched.Status);
        ExpectNoSpend(untouched);

        Assert.Equal((0, 0), (stub.AdaptationCalls, stub.MeaningCalls));
        Assert.Equal(0, batch.UsageTotal.TotalTokens);
        Assert.Equal(0, batch.MeaningUsageTotal.TotalTokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Primera línea.\nSegunda línea.")]
    public async Task MalformedAdaptationResponse_FailsClosed(string malformedContent)
    {
        var stub = new LaneStub { AdaptationContent = malformedContent };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        DirectorAdaptationBatch batch = await service.RunAsync([OptedIn()], "en", "es");

        DirectorAdaptationSegmentResult result = Assert.Single(batch.Segments);
        ExpectBilledAdaptationFailure(result, "Malformed");
        Assert.Equal((1, 0), (stub.AdaptationCalls, stub.MeaningCalls));
    }

    [Fact]
    public async Task ChangedTrue_WhenAdaptationRewritesTheLine_Seg008FormalShape()
    {
        // Live shape: seg-008 formal introduced the usted form "Le" over the
        // register-neutral baseline.
        var stub = new LaneStub { AdaptationContent = "Le veremos en la próxima etapa." };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        var segment = new DirectorAdaptationSegmentInput(
            "seg-008", "We will see you on the next stage.", "Nos vemos en la próxima etapa.",
            DurationSeconds: 2.0, Context: null, new DirectorAdaptationSpec("formal"));

        DirectorAdaptationBatch batch = await service.RunAsync([segment], "en", "es");

        DirectorAdaptationSegmentResult result = Assert.Single(batch.Segments);
        ExpectPassed(result, "Le veremos en la próxima etapa.");
        Assert.True(result.Changed);
        ExpectBothGatesRan(result, stub);
    }

    [Fact]
    public async Task ChangedFalse_WhenModelReturnsLineUnchanged_Seg008InformalShape()
    {
        // Live shape: seg-008 informal and the seg-002 context run both returned
        // their baselines byte-identical. Unchanged is a recorded outcome; the
        // gates still run on the returned line exactly as usual.
        var stub = new LaneStub { AdaptationContent = "Nos vemos en la próxima etapa." };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        var segment = new DirectorAdaptationSegmentInput(
            "seg-008", "We will see you on the next stage.", "Nos vemos en la próxima etapa.",
            DurationSeconds: 2.0, Context: null, new DirectorAdaptationSpec("informal"));

        DirectorAdaptationBatch batch = await service.RunAsync([segment], "en", "es");

        DirectorAdaptationSegmentResult result = batch.Segments.Single();
        ExpectPassed(result, "Nos vemos en la próxima etapa.");
        Assert.False(result.Changed);
        ExpectBothGatesRan(result, stub);
    }

    [Fact]
    public async Task TruncatedCompletion_FailsClosedWithUsageRecorded()
    {
        // A finish_reason of "length" is not an adapted line: even a short
        // prefix that would pass both gates must not report Passed.
        var stub = new LaneStub { AdaptationContent = "Una toma.", AdaptationFinishReason = "length" };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        DirectorAdaptationBatch batch = await service.RunAsync([OptedIn(durationSeconds: 4.0)], "en", "es");

        DirectorAdaptationSegmentResult result = batch.Segments.Single();
        ExpectBilledAdaptationFailure(result, "length");
        Assert.Equal((1, 0), (stub.AdaptationCalls, stub.MeaningCalls));
    }

    [Fact]
    public async Task NullSegmentEntry_FailsTheLoaderWithADiagnostic()
    {
        const string json = """[null]""";
        string tmp = Path.Combine(Path.GetTempPath(), $"director-b2-null-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tmp, json);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DirectorAdaptationService.LoadSegmentsAsync(tmp));
            Assert.Contains("index 0", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    public enum TransportFailure { Adaptation, BodyRead, Meaning }

    [Theory]
    [InlineData(TransportFailure.Adaptation)]
    [InlineData(TransportFailure.BodyRead)]
    [InlineData(TransportFailure.Meaning)]
    public async Task TransportError_FailsTheSegmentAsTerminal(TransportFailure failure)
    {
        bool adaptationFails = failure != TransportFailure.Meaning;
        LaneStub stub = failure switch
        {
            TransportFailure.Adaptation => new LaneStub { AdaptationTransportsError = true },
            TransportFailure.BodyRead => new LaneStub { AdaptationBodyReadFails = true },
            _ => new LaneStub { MeaningTransportsError = true },
        };
        (DirectorAdaptationService service, IDisposable scope) = BuildService(stub);
        using var _ = scope;

        DirectorAdaptationBatch batch = await service.RunAsync([OptedIn()], "en", "es");

        DirectorAdaptationSegmentResult result = Assert.Single(batch.Segments);
        Assert.Equal(DirectorAdaptationStatus.Failed, result.Status);

        if (adaptationFails)
        {
            Assert.NotNull(result.Error);
            Assert.Contains(failure == TransportFailure.BodyRead ? "body read reset" : "HTTP 500", result.Error, StringComparison.Ordinal);
            Assert.Equal((1, 0), (stub.AdaptationCalls, stub.MeaningCalls));
            return;
        }

        Assert.Equal(QaVerdict.Pass, result.TimingVerdict);
        ExpectMeaning(result, MeaningVerdict.Error);
        Assert.Equal((1, 1), (stub.AdaptationCalls, stub.MeaningCalls));
    }
}
