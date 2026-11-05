using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Director.Core;
using static Trackdub.Director.Tests.ServerTestSupport;

namespace Trackdub.Director.Tests;

public sealed class CopilotServiceTests
{
    private static CopilotRequest Request() => new(
        "Prepare a Spanish dub",
        "en",
        "es",
        new CopilotSettingsProposal(),
        [new CopilotSegmentSummary("seg-1", "Hello", 1, "anna", HasFrame: true)]);

    private const string ValidProposal = """
        {
          "template": "qa",
          "explanation": "Run QA first.",
          "settings": { "max_revisions": 2, "tolerance_percent": 12, "target_cps": null, "speed_retake_enabled": false, "speed_cap": 1.1, "emotion_tags_enabled": true },
          "segment_updates": [{ "id": "seg-1", "concepts": ["sign"], "vision_enrichment": true, "director_register": "formal", "sign_source_text": null, "sign_target_text": null }],
          "missing_inputs": [],
          "warnings": []
        }
        """;

    [Fact]
    public void Parse_accepts_whitelisted_proposal()
    {
        CopilotProposal proposal = CopilotService.Parse(
            ValidProposal,
            Request(),
            "muse-spark-1.1",
            new MetaClient.TokenUsage(10, 20, 30));

        Assert.Equal("qa", proposal.Template);
        Assert.Equal(2, proposal.Settings.MaxRevisions);
        Assert.Equal("formal", Assert.Single(proposal.SegmentUpdates).DirectorRegister);
        Assert.Equal(30, proposal.Usage?.TotalTokens);
    }

    [Fact]
    public void Proposal_serializes_explicit_nulls_but_omits_absent_fields()
    {
        CopilotProposal proposal = CopilotService.Parse(
            """{"template":"director","explanation":"x","settings":{},"segment_updates":[{"id":"seg-1","director_register":null,"sign_source_text":"Hola"}],"missing_inputs":[],"warnings":[]}""",
            Request(),
            "muse-spark-1.1",
            null);

        string json = JsonSerializer.Serialize(
            proposal,
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement update = doc.RootElement.GetProperty("segment_updates")[0];

        Assert.Equal(JsonValueKind.Null, update.GetProperty("director_register").ValueKind);
        Assert.Equal("Hola", update.GetProperty("sign_source_text").GetString());
        Assert.False(update.TryGetProperty("sign_target_text", out _));
        Assert.False(update.TryGetProperty("concepts", out _));
    }

    [Theory]
    [InlineData("""{"template":"unknown","explanation":"x","settings":{},"segment_updates":[],"missing_inputs":[],"warnings":[]}""")]
    [InlineData("""{"template":"qa","explanation":"x","settings":{"run_shell":true},"segment_updates":[],"missing_inputs":[],"warnings":[]}""")]
    [InlineData("""{"template":"qa","explanation":"x","settings":{"max_revisions":99},"segment_updates":[],"missing_inputs":[],"warnings":[]}""")]
    [InlineData("""{"template":"qa","explanation":"x","settings":{},"segment_updates":[{"id":"other"}],"missing_inputs":[],"warnings":[]}""")]
    [InlineData("not json")]
    public void Parse_rejects_unknown_or_out_of_range_proposals(string content)
    {
        MetaApiException ex = Assert.Throws<MetaApiException>(() =>
            CopilotService.Parse(content, Request(), "muse-spark-1.1", new MetaClient.TokenUsage(1, 2, 3)));
        Assert.Equal(3, ex.Usage?.TotalTokens);
    }

    [Fact]
    public async Task Endpoint_returns_validated_proposal()
    {
        using var auth = FakeAuth();
        string chatBody = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = ValidProposal } } },
            model = "muse-spark-1.1",
            usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30 },
        });
        using var factory = new ServerFactory(new FixedHandler(chatBody));
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/copilot/propose", Request());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("qa", doc.RootElement.GetProperty("template").GetString());
        Assert.Equal("copilot-proposal-v1", doc.RootElement.GetProperty("prompt_template_version").GetString());
    }

    private sealed class FixedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
