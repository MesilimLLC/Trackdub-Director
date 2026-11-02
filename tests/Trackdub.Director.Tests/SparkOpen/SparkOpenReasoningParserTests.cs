using Trackdub.Director.Core.SparkOpen;

namespace Trackdub.Director.Tests.SparkOpen;

/// <summary>A reasoning-prefixed reply parses to its final answer; a reply with no extractable answer fails closed.</summary>
public sealed class SparkOpenReasoningParserTests
{
    [Fact]
    public void Reasoning_prefixed_json_verdict_parses_to_the_final_object()
    {
        string reply = "<think>\nSubject and action are preserved; numbers match.\n</think>\n" +
                       "My conclusion follows.\n{\"verdict\":\"pass\",\"issues\":[],\"explanation\":\"Meaning preserved.\"}";

        Assert.True(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out string obj));
        Assert.Contains("\"verdict\":\"pass\"", obj, StringComparison.Ordinal);
        Assert.DoesNotContain("think", obj, StringComparison.Ordinal);
    }

    [Fact]
    public void A_brace_inside_the_reasoning_does_not_shadow_the_trailing_answer()
    {
        // The reasoning quotes an example object; the real answer is the last balanced object.
        string reply = "Consider {\"verdict\":\"fail\"} as a counterexample, but actually:\n" +
                       "{\"verdict\":\"pass\",\"issues\":[],\"explanation\":\"ok\"}";

        Assert.True(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out string obj));
        Assert.Contains("\"verdict\":\"pass\"", obj, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fenced_final_object_is_unwrapped()
    {
        string reply = "Here is the answer:\n```json\n{\"verdict\":\"fail\",\"issues\":[\"number\"],\"explanation\":\"two vs three\"}\n```";

        Assert.True(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out string obj));
        Assert.Contains("\"verdict\":\"fail\"", obj, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_final_line_is_returned_as_the_answer_when_there_is_no_json()
    {
        // Translation path: the final answer is the plain translated line.
        string reply = "<think>\nRender naturally, keep the name Ashley.\n</think>\nHola, Ashley.";

        Assert.True(SparkOpenReasoningParser.TryExtractFinalAnswer(reply, out string answer));
        Assert.Equal("Hola, Ashley.", answer);
    }

    [Fact]
    public void Final_answer_prefers_a_trailing_json_object_over_the_last_text_line()
    {
        string reply = "thinking...\n{\"translation\":\"Hola\"}";
        Assert.True(SparkOpenReasoningParser.TryExtractFinalAnswer(reply, out string answer));
        Assert.Equal("{\"translation\":\"Hola\"}", answer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<think>\nonly reasoning, no answer ever emitted\n</think>")]
    [InlineData("<think></think>\n   ")]
    public void A_reply_with_no_extractable_answer_fails_closed(string? reply)
    {
        Assert.False(SparkOpenReasoningParser.TryExtractFinalAnswer(reply, out string answer));
        Assert.Equal(string.Empty, answer);
    }

    [Theory]
    [InlineData("just prose, no json object at all")]
    [InlineData("<think>reasoning</think>\nnot a json verdict line")]
    public void A_reply_with_no_json_object_yields_no_final_json_object(string reply)
    {
        // The judge path needs a JSON verdict specifically; a prose-only reply has none.
        Assert.False(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out _));
    }
}
