namespace Terminal.Bos;

public abstract record InvocationOutcome;

public sealed record InlineResult(object? Value) : InvocationOutcome;

public sealed record BosError(string Code, string Message);

public sealed class BosEngine
{
    public const string Version = "0.1";
    private readonly TerminalStatusCatalog _status;

    public BosEngine(TerminalStatusCatalog status) =>
        _status = status ?? throw new ArgumentNullException(nameof(status));

    public InvocationOutcome Invoke(string commandLine)
    {
        string[] words = (commandLine ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return new InlineResult(null);

        return words[0].ToUpperInvariant() switch
        {
            "VER" when words.Length == 1 => new InlineResult($"BOS {Version}"),
            "STATUS" => Status(words),
            _ => new InlineResult(new BosError("BOS001", $"Unknown BOS command '{words[0]}'.")),
        };
    }

    private InvocationOutcome Status(string[] words)
    {
        TerminalOperationalStatus snapshot = _status.Capture();
        if (words.Length == 1) return new InlineResult(snapshot);
        if (words.Length != 2)
            return new InlineResult(new BosError("BOS002", "Usage: STATUS [BOS|REMEDY|SESSIONS]"));

        return words[1].ToUpperInvariant() switch
        {
            "BOS" => new InlineResult(snapshot.Bos),
            "REMEDY" => new InlineResult(snapshot.Remedy),
            "SESSIONS" or "ROUTER" => new InlineResult(snapshot.Router),
            _ => new InlineResult(new BosError("BOS002", "Usage: STATUS [BOS|REMEDY|SESSIONS]")),
        };
    }
}
