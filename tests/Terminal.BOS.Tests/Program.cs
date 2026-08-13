using Terminal.Bos;

var tests = new (string Name, Action Body)[]
{
    ("cold status is honest", ColdStatusIsHonest),
    ("status projections share one snapshot model", StatusProjectionsShareModel),
    ("published lifecycle remains identity layered", PublishedLifecycleIsLayered),
    ("version and unknown command behavior", VersionAndUnknownCommand),
};

int failures = 0;
foreach ((string name, Action body) in tests)
{
    try { body(); Console.WriteLine($"PASS  {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL  {name}: {error.Message}"); }
}
Console.WriteLine($"Terminal.Bos contract: {tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static void ColdStatusIsHonest()
{
    var catalog = new TerminalStatusCatalog(BosEngine.Version);
    TerminalOperationalStatus status = catalog.Capture();
    Equal(OperationalState.Starting, status.Bos.State);
    Equal(OperationalState.Absent, status.Remedy.State);
    Equal(OperationalState.Absent, status.Router.State);
    Equal(ConsoleBackend.None, status.Router.Backend);
    Equal(0, status.Router.Routes.Count);
}

static void StatusProjectionsShareModel()
{
    var catalog = new TerminalStatusCatalog(BosEngine.Version);
    catalog.PublishBos(new BosStatus(OperationalState.Ready, BosEngine.Version));
    var engine = new BosEngine(catalog);
    var all = Value<TerminalOperationalStatus>(engine.Invoke("status"));
    var bos = Value<BosStatus>(engine.Invoke("STATUS BOS"));
    var remedy = Value<WorkerSupervisorStatus>(engine.Invoke("status remedy"));
    Equal(all.Bos, bos);
    Equal(all.Remedy, remedy);
}

static void PublishedLifecycleIsLayered()
{
    var catalog = new TerminalStatusCatalog(BosEngine.Version);
    Guid session = Guid.NewGuid();
    catalog.PublishWorkerSupervisor(new WorkerSupervisorStatus(
        OperationalState.Ready, 41, ReadyReceived: true));
    catalog.PublishRouter(new ConsoleRouterStatus(
        OperationalState.Ready, ConsoleBackend.Pty, 41,
        [new ConsoleRouteStatus(session, 1001, OperationalState.Ready, 80, 24, 321)]));
    TerminalOperationalStatus status = catalog.Capture();
    Equal((ulong)41, status.Remedy.Generation!.Value);
    Equal((ulong)1001, status.Router.Routes[0].RouteId);
    Equal(session, status.Router.Routes[0].SessionId);
    True(status.Router.Routes[0].RouteId != status.Remedy.Generation);
}

static void VersionAndUnknownCommand()
{
    var engine = new BosEngine(new TerminalStatusCatalog(BosEngine.Version));
    Equal("BOS 0.1", Value<string>(engine.Invoke("ver")));
    Equal("BOS001", Value<BosError>(engine.Invoke("dance")).Code);
    Equal("BOS002", Value<BosError>(engine.Invoke("status nonsense")).Code);
}

static T Value<T>(InvocationOutcome outcome) =>
    outcome is InlineResult { Value: T value } ? value :
    throw new InvalidOperationException($"Expected {typeof(T).Name} inline result.");

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected <{expected}> but found <{actual}>.");
}
