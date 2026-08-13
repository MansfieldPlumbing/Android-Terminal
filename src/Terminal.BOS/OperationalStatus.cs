namespace Terminal.BOS;

public enum OperationalState
{
    Absent,
    Starting,
    Ready,
    Busy,
    Quiescing,
    Retired,
    Degraded,
    Fault,
}

public enum ConsoleBackend
{
    None,
    ConPty,
    Pty,
}

public sealed record BOSStatus(OperationalState State, string Version, string? Detail = null);

public sealed record WorkerSupervisorStatus(
    OperationalState State,
    ulong? Generation = null,
    bool ReadyReceived = false,
    bool QuiesceAcknowledged = false,
    bool WorkerExited = false,
    bool TerminalChannelObserved = false,
    string? Detail = null);

public sealed record ConsoleRouteStatus(
    Guid SessionId,
    ulong RouteId,
    OperationalState State,
    int Columns,
    int Rows,
    int? ProcessId = null,
    int? ExitCode = null);

public sealed record ConsoleRouterStatus(
    OperationalState State,
    ConsoleBackend Backend,
    ulong? WorkerGeneration,
    IReadOnlyList<ConsoleRouteStatus> Routes,
    string? Detail = null);

public sealed record TerminalOperationalStatus(
    DateTimeOffset ObservedAt,
    BOSStatus BOS,
    WorkerSupervisorStatus Remedy,
    ConsoleRouterStatus Router);

public sealed class TerminalStatusCatalog
{
    private readonly object _gate = new();
    private TerminalOperationalStatus _current;

    public TerminalStatusCatalog(string bosVersion)
    {
        _current = new TerminalOperationalStatus(
            DateTimeOffset.UtcNow,
            new BOSStatus(OperationalState.Starting, bosVersion),
            new WorkerSupervisorStatus(OperationalState.Absent, Detail: "No subordinate worker generation has been admitted."),
            new ConsoleRouterStatus(OperationalState.Absent, ConsoleBackend.None, null, [],
                "No console router worker is active."));
    }

    public TerminalOperationalStatus Capture()
    {
        lock (_gate) return _current with { ObservedAt = DateTimeOffset.UtcNow };
    }

    public void PublishBOS(BOSStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate) _current = _current with { ObservedAt = DateTimeOffset.UtcNow, BOS = status };
    }

    public void PublishWorkerSupervisor(WorkerSupervisorStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate) _current = _current with { ObservedAt = DateTimeOffset.UtcNow, Remedy = status };
    }

    public void PublishRouter(ConsoleRouterStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate) _current = _current with {
            ObservedAt = DateTimeOffset.UtcNow,
            Router = status with { Routes = status.Routes.ToArray() },
        };
    }
}
