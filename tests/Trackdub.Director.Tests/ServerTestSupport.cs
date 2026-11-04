using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Shared fakes, handlers and assertion helpers for the server endpoint test classes.
/// </summary>
internal static class ServerTestSupport
{
    internal static HttpResponseMessage JsonOk(object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    // Verbatim raw body of the live muse-spark-1.1 scene-vision-v1 probe fixture.
    internal const string ProbeVisionResponseBody = """{"id":"chatcmpl-01a186b2-eade-74aa-a0f6-6bd0b8d71421","choices":[{"finish_reason":"stop","index":0,"message":{"content":"{\"scene_summary\": \"A woman stands outside a coffee shop entrance talking on her cell phone while smiling.\", \"emotion_tone_label\": \"cheerful\", \"speaker_description\": \"a smiling woman in a knit beanie and black winter coat talking on a phone\", \"on_screen_text\": [\"COFFEE SHOP\", \"STORE HOURS\", \"MONDAY - FRIDAY\", \"6:30AM - 8:00PM\", \"SATURDAY & SUNDAY\", \"8:00AM - 8:00PM\"], \"burned_in_text\": [\"VOA Learning English\", \"Okay. My apartment is across from a big department store.\"]}","refusal":null,"role":"assistant"},"logprobs":null}],"created":1793261234,"model":"muse-spark-1.1","object":"chat.completion","usage":{"completion_tokens":756,"prompt_tokens":2878,"total_tokens":3634,"completion_tokens_details":{"reasoning_tokens":611},"prompt_tokens_details":{"cached_tokens":0}}}""";

    internal sealed record ChatReply(string ModelId, string Content, int PromptTokens = 10, int CompletionTokens = 5);

    internal static HttpResponseMessage ChatOk(ChatReply reply)
    {
        (string modelId, string content, int promptTokens, int completionTokens) = reply;
        var payload = new
        {
            id = $"chatcmpl-test-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = 1234567890L,
            model = modelId,
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens, total_tokens = promptTokens + completionTokens },
        };
        return JsonOk(payload);
    }

    internal sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    internal static IDisposable EnvShim(string key, string value)
    {
        string? prev = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
        return new Restore(() => Environment.SetEnvironmentVariable(key, prev));
    }

    internal sealed class Restore : IDisposable
    {
        private readonly Action _restore;
        public Restore(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }

    internal static IDisposable FakeAuth(string model = "muse-spark-1.1")
    {
        var a = EnvShim("META_API_KEY", "test-fake-key");
        var b = EnvShim("META_MODEL", model);
        var c = EnvShim("META_MODEL_API_BASE_URL", "https://api.meta.ai/v1");
        var d = EnvShim("META_REASONING_EFFORT", "low");
        return new Composite(a, b, c, d);
    }

    internal sealed class Composite : IDisposable
    {
        private readonly IDisposable[] _items;
        public Composite(params IDisposable[] items) => _items = items;
        public void Dispose() { foreach (IDisposable d in _items) d.Dispose(); }
    }

    internal sealed class ServerFactory : WebApplicationFactory<Program>
    {
        private readonly HttpMessageHandler _handler;
        public ServerFactory(HttpMessageHandler handler) => _handler = handler;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<HttpClient>();
                services.AddSingleton<HttpClient>(_ => new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(60) });
            });
        }
    }

    internal sealed class ChatStubOptions
    {
        public string ModelId { get; init; } = "muse-spark-1.1";
        public int PromptTokens { get; init; } = 10;
        public int CompletionTokens { get; init; } = 5;
        public int MeaningPrompt { get; init; } = 7;
        public int MeaningComp { get; init; } = 4;
    }

    internal static HttpMessageHandler SequencedChatHandler(Queue<string> translations, ChatStubOptions? options = null)
    {
        return DualHandler(translations, new Queue<string>([JsonSerializer.Serialize(new { verdict = "pass", issues = Array.Empty<string>(), explanation = "ok" })]), options ?? new ChatStubOptions());
    }

    internal static HttpMessageHandler DualHandler(
        Queue<string> translations,
        Queue<string> meaningJsons,
        ChatStubOptions options)
    {
        return new DelegatingHandlerStub(async (request, cancellationToken) =>
        {
            string? path = request.RequestUri?.AbsolutePath;
            if (path is not null && path.EndsWith("/models", StringComparison.Ordinal))
            {
                var modelsPayload = new
                {
                    @object = "list",
                    data = new[]
                    {
                        new { id = "muse-spark-1.1", @object = "model", created = 123L, owned_by = "meta" },
                        new { id = "llama-4-maverick", @object = "model", created = 124L, owned_by = "meta" },
                    },
                };
                return JsonOk(modelsPayload);
            }

            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            bool isMeaning = body.Contains(MeaningGate.MeaningMarker, StringComparison.Ordinal);

            if (isMeaning)
            {
                if (meaningJsons.Count == 0)
                {
                    throw new InvalidOperationException("No more canned meaning JSON queued.");
                }

                string mj = meaningJsons.Dequeue();
                return ChatOk(new ChatReply(options.ModelId, mj, options.MeaningPrompt, options.MeaningComp));
            }

            if (translations.Count == 0)
            {
                throw new InvalidOperationException("No more canned translations queued for chat/completions.");
            }

            string translation = translations.Dequeue();
            return ChatOk(new ChatReply(options.ModelId, translation, options.PromptTokens, options.CompletionTokens));
        });
    }

    internal static HttpResponseMessage UpstreamError(HttpStatusCode status) => new(status)
    {
        Content = new StringContent("{\"error\":\"upstream failure\"}", Encoding.UTF8, "application/json"),
    };

    internal static async Task<(HttpStatusCode Status, JsonElement Root)> PostRawAsync(HttpClient client, string path, string body)
    {
        HttpResponseMessage resp = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        string json = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        return (resp.StatusCode, doc.RootElement.Clone());
    }

    internal const string CannedTranscribeBody = """{"sessionId":"voyager/transcribe/API","transcript":"Hi, Ashley. Anna, which coffee shop?","audioDurationMs":6060,"turns":[{"turnId":0,"startMs":60,"endMs":1260,"transcript":"Hi, Ashley.","speaker":"A"},{"turnId":1,"startMs":1660,"endMs":6060,"transcript":"Anna, which coffee shop?","speaker":"B"}]}""";

    // Checks dotted-path string properties (numeric segments index arrays), e.g. ("meaning.verdict", "Pass").
    internal static void ExpectStrings(JsonElement element, params (string Path, string Expected)[] expected)
    {
        foreach ((string path, string want) in expected)
        {
            JsonElement node = element;
            foreach (string part in path.Split('.'))
            {
                node = int.TryParse(part, out int index) ? node[index] : node.GetProperty(part);
            }

            Assert.Equal(want, node.GetString());
        }
    }
}
