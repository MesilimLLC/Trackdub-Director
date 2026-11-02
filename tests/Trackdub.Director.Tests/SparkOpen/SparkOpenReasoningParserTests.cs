using Trackdub.Director.Core.SparkOpen;

namespace Trackdub.Director.Tests.SparkOpen;

/// <summary>A reasoning-prefixed reply parses to its final answer; a reply with no extractable answer fails closed.</summary>
public sealed class SparkOpenReasoningParserTests
{
    [Fact]
    public void Reasoning_prefixed_json_verdict_parses_to_the_final_object()
    {
        string reply = "<thinking>\nSubject and action are preserved; numbers match.\n</thinking>\n" +
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
        string reply = "<thinking>\nRender naturally, keep the name Ashley.\n</thinking>\nHola, Ashley.";

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
    [InlineData("<thinking>\nonly reasoning, no answer ever emitted\n</thinking>")]
    [InlineData("<thinking></thinking>\n   ")]
    public void A_reply_with_no_extractable_answer_fails_closed(string? reply)
    {
        Assert.False(SparkOpenReasoningParser.TryExtractFinalAnswer(reply, out string answer));
        Assert.Equal(string.Empty, answer);
    }

    [Theory]
    [InlineData("just prose, no json object at all")]
    [InlineData("<thinking>reasoning</thinking>\nnot a json verdict line")]
    public void A_reply_with_no_json_object_yields_no_final_json_object(string reply)
    {
        // The judge path needs a JSON verdict specifically; a prose-only reply has none.
        Assert.False(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out _));
    }

    [Fact]
    public void Multiple_think_blocks_are_all_stripped()
    {
        // Reasoning models sometimes emit more than one block; each must be stripped so the answer
        // below them wins and reasoning prose never becomes the answer.
        string reply = "<thinking>\nFirst reasoning pass.\n</thinking>\nSome notes.\n<thinking>\nSecond pass, still reasoning.\n</thinking>\n{\"verdict\":\"pass\",\"issues\":[],\"explanation\":\"ok\"}";

        Assert.True(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out string obj));
        Assert.Contains("\"verdict\":\"pass\"", obj, StringComparison.Ordinal);
        Assert.DoesNotContain("First reasoning", obj, StringComparison.Ordinal);
        Assert.DoesNotContain("Second pass", obj, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unterminated_think_block_yields_no_final_answer()
    {
        // Generation stopped inside a think block: truncated reasoning must never be taken as an answer.
        string reply = "<thinking>\nSubject preserved, numbers match...";

        Assert.False(SparkOpenReasoningParser.TryExtractFinalAnswer(reply, out string answer));
        Assert.Equal(string.Empty, answer);
        Assert.False(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out _));
    }

    [Fact]
    public void A_verdict_only_inside_the_reasoning_is_not_the_final_verdict()
    {
        // The judge must not take a verdict quoted inside a completed think block as the final answer.
        string reply = "<thinking>Example: {\"verdict\":\"pass\",\"issues\":[],\"explanation\":\"example\"}</thinking>";

        Assert.False(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out _));
    }

    [Fact]
    public void Bare_marker_reasoning_blocks_are_stripped_too()
    {
        // Some deployments emit the reasoning delimiters without brackets (" thinking" / " response").
        string reply = " thinking\nWeigh subject and numbers.\n response\n{\"verdict\":\"pass\",\"issues\":[],\"explanation\":\"ok\"}";

        Assert.True(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out string obj));
        Assert.Contains("\"verdict\":\"pass\"", obj, StringComparison.Ordinal);
        Assert.DoesNotContain("Weigh subject", obj, StringComparison.Ordinal);
    }

    [Fact]
    public void Judge_rejects_a_verdict_object_that_is_not_at_the_end_of_the_reply()
    {
        // A verdict-shaped object with prose after it is reasoning, not the final verdict.
        string reply = "Reasoning quotes a sample verdict: {\"verdict\":\"fail\",\"issues\":[],\"explanation\":\"x\"}\nThen my conclusion follows.";

        Assert.False(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out _, requireEndOfReply: true));
    }

    [Fact]
    public void Judge_accepts_a_multiline_final_verdict_object_at_the_end_of_the_reply()
    {
        string reply = "Reasoning prose above.\n{\n\"verdict\": \"pass\",\n\"issues\": [],\n\"explanation\": \"ok\"\n}";

        Assert.True(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out string obj, requireEndOfReply: true));
        Assert.Contains("\"verdict\": \"pass\"", obj, StringComparison.Ordinal);
    }

    [Fact]
    public void A_multiline_plain_answer_is_kept_whole_when_there_is_no_json()
    {
        string reply = "Hola, Ashley.\nNos vemos pronto.";

        Assert.True(SparkOpenReasoningParser.TryExtractFinalAnswer(reply, out string answer));
        Assert.Equal("Hola, Ashley.\nNos vemos pronto.", answer);
    }

    [Fact]
    public void An_unmatched_brace_in_reasoning_does_not_hide_a_later_final_object()
    {
        string reply = "A stray { without a close.\n{\n\"verdict\":\"pass\",\n\"issues\":[],\n\"explanation\":\"ok\"\n}";

        Assert.True(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out string obj));
        Assert.Contains("\"verdict\":\"pass\"", obj, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quoted_example_object_does_not_displace_a_trailing_plain_answer()
    {
        // A translation reply that quotes an example object and then gives the real plain-text answer
        // must not return the example's translation: the object is mid-reply, not the final answer.
        string reply = "Example: {\"translation\":\"Si\"}\nNo, gracias.";

        Assert.True(SparkOpenReasoningParser.TryExtractFinalAnswer(reply, out string answer));
        Assert.Contains("No, gracias.", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"translation\"", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_sentences_starting_with_thinking_or_response_are_not_markers()
    {
        // A translated line that merely begins with the word "thinking"/"response" is never rewritten
        // into a reasoning marker, so it survives as the answer instead of becoming a truncated block.
        Assert.True(SparkOpenReasoningParser.TryExtractFinalAnswer("Thinking of you.\nNos vemos.", out string thinking));
        Assert.Equal("Thinking of you.\nNos vemos.", thinking);

        Assert.True(SparkOpenReasoningParser.TryExtractFinalAnswer("Response to your request: No.\nAdios.", out string response));
        Assert.Equal("Response to your request: No.\nAdios.", response);

        // A standalone bare marker line is still a marker and is stripped.
        string marked = " thinking\nreasoning text\n response\nHola.";
        Assert.True(SparkOpenReasoningParser.TryExtractFinalAnswer(marked, out string stripped));
        Assert.Equal("Hola.", stripped);
    }

    [Fact]
    public void A_verdict_nested_in_a_truncated_wrapper_is_not_the_final_verdict()
    {
        // The reply is truncated inside the outer object; the nested verdict must not be scored as
        // the final top-level verdict (the completed wrapper has no verdict property and is an Error).
        string reply = "{\"draft\":{\"verdict\":\"pass\",\"issues\":[]}";

        Assert.False(SparkOpenReasoningParser.TryExtractFinalJsonObject(reply, out _, requireEndOfReply: true));
    }
}
