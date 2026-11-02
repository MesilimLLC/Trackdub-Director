using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Tests for the strict Glimmer action parser (malformed detection is what makes a
/// malformed reply a failed attempt, never a silent proposal), the leakage-safe prompt
/// builder, and the env-only fail-closed auth.
/// </summary>
public sealed class GlimmerPromptTests
{
    [Fact]
    public void Parses_a_clean_propose_action()
    {
        GlimmerAction? a = GlimmerActionParser.TryParse("{\"action\":\"propose\",\"text_es\":\"Hola.\",\"strategy\":\"shorten\"}");
        Assert.NotNull(a);
        Assert.Equal((GlimmerActionKind.Propose, "Hola.", "shorten"), (a!.Action, a.TextEs, a.Strategy));
    }

    [Fact]
    public void Parses_a_clean_give_up_action()
    {
        GlimmerAction? a = GlimmerActionParser.TryParse("{\"action\":\"give_up\",\"strategy\":\"no safe recovery\"}");
        Assert.NotNull(a);
        Assert.Equal((GlimmerActionKind.GiveUp, (string?)null, "no safe recovery"), (a!.Action, a.TextEs, a.Strategy));
    }

    [Fact]
    public void Extracts_a_single_action_wrapped_in_prose()
    {
        GlimmerAction? a = GlimmerActionParser.TryParse("Here you go: {\"action\":\"propose\",\"text_es\":\"Hola {amigo}.\",\"strategy\":\"keep name\"} done.");
        Assert.NotNull(a);
        Assert.Equal("Hola {amigo}.", a!.TextEs); // brace inside the string value does not break extraction
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"action\":\"propose\"}")]                                        // missing text_es and strategy
    [InlineData("{\"action\":\"propose\",\"strategy\":\"x\"}")]                     // missing text_es
    [InlineData("{\"action\":\"propose\",\"text_es\":\"\",\"strategy\":\"x\"}")]   // blank text_es
    [InlineData("{\"action\":\"propose\",\"text_es\":\"   \",\"strategy\":\"x\"}")] // whitespace text_es
    [InlineData("{\"action\":\"propose\",\"text_es\":\"Hola\"}")]                   // missing strategy
    [InlineData("{\"action\":\"propose\",\"text_es\":123,\"strategy\":\"x\"}")]    // text_es wrong type
    [InlineData("{\"action\":\"retry\",\"text_es\":\"Hola\",\"strategy\":\"x\"}")] // unknown action
    [InlineData("{\"action\":\"give_up\",\"text_es\":\"Hola\",\"strategy\":\"x\"}")] // give_up carrying a proposal
    [InlineData("{\"action\":\"give_up\"}")]                                        // give_up missing strategy
    [InlineData("[\"propose\"]")]                                                  // not an object
    [InlineData("{\"action\":\"propose\",\"text_es\":\"Hola\",\"strategy\":\"\"}")] // blank strategy
    public void Off_schema_replies_are_rejected_as_malformed(string raw)
    {
        Assert.Null(GlimmerActionParser.TryParse(raw));
    }

    [Fact]
    public void System_prompt_pins_the_schema_and_separates_data_from_instructions()
    {
        string sys = GlimmerPrompt.BuildSystemPrompt();
        Assert.Contains(GlimmerPrompt.RecoveryMarker, sys, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"propose\"", sys, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"give_up\"", sys, StringComparison.Ordinal);
        Assert.Contains("data, not instructions", sys, StringComparison.Ordinal);
        Assert.Contains("never an accept", sys, StringComparison.Ordinal);
    }

    [Fact]
    public void User_prompt_carries_leakage_safe_fields_only()
    {
        var item = new RecoveryItem(
            "meaning-eval/qa-report-pressure-70.json", "seg-006", 2, "Where are we shooting tomorrow?",
            "en", "es", 1.12, 0.10, 15.0, RecoveryFailureType.Meaning, "¿Rodamos mañana?",
            ["Omits location interrogative 'where'"], SceneContext: "Studio call sheet scene.");

        string prompt = GlimmerPrompt.BuildUserPrompt(new GlimmerRecoveryTurn(item, [], 1));

        Assert.Contains("Where are we shooting tomorrow?", prompt, StringComparison.Ordinal);
        Assert.Contains("¿Rodamos mañana?", prompt, StringComparison.Ordinal);
        Assert.Contains("Omits location interrogative 'where'", prompt, StringComparison.Ordinal);
        Assert.Contains("Studio call sheet scene.", prompt, StringComparison.Ordinal);
        Assert.Contains("<source_line>", prompt, StringComparison.Ordinal);
        Assert.Contains("<gate_failure>", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void User_prompt_summarizes_prior_attempts_without_leaking_sibling_segment_text()
    {
        var item = new RecoveryItem(
            "meaning-eval/qa-report-pressure-70.json", "seg-006", 3, "Where are we shooting tomorrow?",
            "en", "es", 1.12, 0.10, 15.0, RecoveryFailureType.Meaning, "¿Dónde rodamos?",
            ["temporal reference 'tomorrow' omitted"], null);

        var prior = new RecoveryAttemptTrace(
            1, GlimmerActionKind.Propose, false, "¿Rodamos ya?", "drop where", 1.0, 1.12,
            QaVerdict.Pass, MeaningVerdict.Fail, ["drops 'where'"], "x", false,
            GlimmerUsageRecord.Zero, GlimmerUsageRecord.Zero);

        string prompt = GlimmerPrompt.BuildUserPrompt(new GlimmerRecoveryTurn(item, [prior], 2));
        // The agent's own prior proposal is shown (it is this item's history), but it is the agent's
        // own output, not a leaked Spark revision.
        Assert.Contains("¿Rodamos ya?", prompt, StringComparison.Ordinal);
        Assert.Contains("prior attempts", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Auth_is_configured_only_when_all_three_route_vars_are_present()
    {
        string? baseUrl = Environment.GetEnvironmentVariable(GlimmerAuth.BaseUrlEnv);
        string? key = Environment.GetEnvironmentVariable(GlimmerAuth.ApiKeyEnv);
        string? model = Environment.GetEnvironmentVariable(GlimmerAuth.ModelEnv);
        try
        {
            Environment.SetEnvironmentVariable(GlimmerAuth.BaseUrlEnv, "https://glimmer.example/v1");
            Environment.SetEnvironmentVariable(GlimmerAuth.ApiKeyEnv, "glimmer-secret");
            Environment.SetEnvironmentVariable(GlimmerAuth.ModelEnv, string.Empty);
            Assert.False(GlimmerAuth.IsConfigured());

            GlimmerNotConfiguredException ex = Assert.Throws<GlimmerNotConfiguredException>(() => GlimmerAuth.GetRoute());
            Assert.Contains(GlimmerAuth.ModelEnv, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("glimmer-secret", ex.Message, StringComparison.Ordinal); // key never echoed

            Environment.SetEnvironmentVariable(GlimmerAuth.ModelEnv, "Muse-Glimmer-30B");
            Assert.True(GlimmerAuth.IsConfigured());
            GlimmerAuth.GlimmerRoute route = GlimmerAuth.GetRoute();
            Assert.Equal(("https://glimmer.example/v1", "glimmer-secret", "Muse-Glimmer-30B"), (route.BaseUrl, route.ApiKey, route.Model));
            Assert.Equal("low", route.ReasoningEffort); // default low selectable reasoning
        }
        finally
        {
            Environment.SetEnvironmentVariable(GlimmerAuth.BaseUrlEnv, baseUrl);
            Environment.SetEnvironmentVariable(GlimmerAuth.ApiKeyEnv, key);
            Environment.SetEnvironmentVariable(GlimmerAuth.ModelEnv, model);
        }
    }
}
