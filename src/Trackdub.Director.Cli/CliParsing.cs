internal readonly record struct CommandArgs(IReadOnlyList<string> Values)
{
    public int Count => Values.Count;

    public CommandArgs Rest() => new(Values.Skip(1).ToArray());

    public string[] ToArray() => Values.ToArray();
}

internal readonly record struct OptionValue(string Text);

internal readonly record struct FilePath(string Value)
{
    public string FullPath => Path.GetFullPath(Value);
}

internal readonly record struct ParseError(string Value);

internal enum ParseStatus
{
    Parsed,
    Help,
    Error,
}

internal sealed class ParseResult<T>
    where T : class
{
    public ParseStatus Status { get; private init; }

    public T? Value { get; private init; }

    public ParseError? Error { get; private init; }

    public static ParseResult<T> Success(T value) => new() { Status = ParseStatus.Parsed, Value = value };

    public static ParseResult<T> Help() => new() { Status = ParseStatus.Help };

    public static ParseResult<T> Failure(ParseError error) => new() { Status = ParseStatus.Error, Error = error };
}
