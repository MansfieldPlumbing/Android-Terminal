using Terminal.BOS;

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
Console.WriteLine($"Terminal.BOS contract: {tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static void ColdStatusIsHonest()
{
    var catalog = new TerminalStatusCatalog(BOSEngine.Version);
    TerminalOperationalStatus status = catalog.Capture();
    Equal(OperationalState.Starting, status.BOS.State);
    Equal(OperationalState.Absent, status.Remedy.State);
    Equal(OperationalState.Absent, status.Router.State);
    Equal(ConsoleBackend.None, status.Router.Backend);
    Equal(0, status.Router.Routes.Count);
}

static void StatusProjectionsShareModel()
{
    var catalog = new TerminalStatusCatalog(BOSEngine.Version);
    catalog.PublishBOS(new BOSStatus(OperationalState.Ready, BOSEngine.Version));
    var engine = new BOSEngine(catalog);
    var all = Value<TerminalOperationalStatus>(engine.Invoke("status"));
    var bos = Value<BOSStatus>(engine.Invoke("STATUS BOS"));
    var remedy = Value<WorkerSupervisorStatus>(engine.Invoke("status remedy"));
    Equal(all.BOS, bos);
    Equal(all.Remedy, remedy);
}

static void PublishedLifecycleIsLayered()
{
    var catalog = new TerminalStatusCatalog(BOSEngine.Version);
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
    var engine = new BOSEngine(new TerminalStatusCatalog(BOSEngine.Version));
    Equal("BOS 0.1", Value<string>(engine.Invoke("ver")));
    Equal("BOS001", Value<BOSError>(engine.Invoke("dance")).Code);
    Equal("BOS002", Value<BOSError>(engine.Invoke("status nonsense")).Code);
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
