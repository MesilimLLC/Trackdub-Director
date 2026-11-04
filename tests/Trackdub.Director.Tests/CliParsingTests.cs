extern alias directorcli;

namespace Trackdub.Director.Tests;

/// <summary>
/// Tests for the CLI's shared parsing primitives (internal to the CLI assembly,
/// referenced through the <c>directorcli</c> alias). Pure value types: no process,
/// no console, no environment.
/// </summary>
public sealed class CliParsingTests
{
    [Fact]
    public void CommandArgs_reports_its_count_and_values()
    {
        var args = new directorcli::CommandArgs(["qa", "--input", "x.json"]);

        Assert.Equal(3, args.Count);
        Assert.Equal(new[] { "qa", "--input", "x.json" }, args.ToArray());
    }

    [Fact]
    public void CommandArgs_rest_drops_the_verb()
    {
        var args = new directorcli::CommandArgs(["translate", "--from", "en"]);

        directorcli::CommandArgs rest = args.Rest();

        Assert.Equal(2, rest.Count);
        Assert.Equal(new[] { "--from", "en" }, rest.ToArray());
    }

    [Fact]
    public void CommandArgs_rest_of_an_empty_command_is_empty()
    {
        Assert.Equal(0, new directorcli::CommandArgs([]).Rest().Count);
    }

    [Fact]
    public void FilePath_resolves_a_relative_path_to_an_absolute_one()
    {
        var file = new directorcli::FilePath(Path.Combine("samples", "segments.json"));

        Assert.True(Path.IsPathFullyQualified(file.FullPath));
        Assert.EndsWith("segments.json", file.FullPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResult_success_carries_the_value_only()
    {
        directorcli::ParseResult<SampleValue> result =
            directorcli::ParseResult<SampleValue>.Success(new SampleValue("v"));

        Assert.Equal(directorcli::ParseStatus.Parsed, result.Status);
        Assert.Equal("v", result.Value!.Text);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ParseResult_help_carries_neither_a_value_nor_an_error()
    {
        directorcli::ParseResult<SampleValue> result = directorcli::ParseResult<SampleValue>.Help();

        Assert.Equal(directorcli::ParseStatus.Help, result.Status);
        Assert.Null(result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ParseResult_failure_carries_the_error_text()
    {
        directorcli::ParseResult<SampleValue> result =
            directorcli::ParseResult<SampleValue>.Failure(new directorcli::ParseError("Unknown option '--nope'."));

        Assert.Equal(directorcli::ParseStatus.Error, result.Status);
        Assert.Null(result.Value);
        Assert.Equal("Unknown option '--nope'.", result.Error!.Value.Value);
    }

    [Fact]
    public void OptionValue_carries_its_text()
    {
        Assert.Equal("x.json", new directorcli::OptionValue("x.json").Text);
    }

    [Fact]
    public void TranslateOptionFlags_maps_each_flag_both_ways()
    {
        Assert.True(directorcli::TranslateOptionFlags.TryGet("--input", out directorcli::TranslateOption input));
        Assert.Equal(directorcli::TranslateOption.Input, input);
        Assert.Equal("--input", directorcli::TranslateOptionFlags.ToFlag(directorcli::TranslateOption.Input));

        Assert.True(directorcli::TranslateOptionFlags.TryGet("--to", out directorcli::TranslateOption to));
        Assert.Equal(directorcli::TranslateOption.To, to);
        Assert.Equal("--to", directorcli::TranslateOptionFlags.ToFlag(directorcli::TranslateOption.To));
    }

    [Fact]
    public void TranslateOptionFlags_rejects_an_unknown_flag()
    {
        Assert.False(directorcli::TranslateOptionFlags.TryGet("--nope", out _));
    }

    [Fact]
    public void TranslateArguments_default_to_english_and_spanish()
    {
        var args = new directorcli::TranslateArguments();

        Assert.Null(args.Input);
        Assert.Null(args.Output);
        Assert.Equal("en", args.From);
        Assert.Equal("es", args.To);
    }

    [Fact]
    public void TranslateArguments_apply_each_flag_to_its_field()
    {
        var args = new directorcli::TranslateArguments();

        Assert.True(args.TryApply(directorcli::TranslateOption.Input, new directorcli::OptionValue("in.json"), out directorcli::ParseError? inputError));
        Assert.Null(inputError);
        Assert.Equal("in.json", args.Input);

        Assert.True(args.TryApply(directorcli::TranslateOption.Output, new directorcli::OptionValue("out.json"), out _));
        Assert.Equal("out.json", args.Output);

        Assert.True(args.TryApply(directorcli::TranslateOption.From, new directorcli::OptionValue("fr"), out _));
        Assert.Equal("fr", args.From);

        Assert.True(args.TryApply(directorcli::TranslateOption.To, new directorcli::OptionValue("de"), out _));
        Assert.Equal("de", args.To);
    }

    private sealed class SampleValue(string text)
    {
        public string Text { get; } = text;
    }
}
