using System;

namespace Subsystem;

public enum LifecycleState { Spawning, Active, Exiting }

public class PluginInstance
{
    public LifecycleState State { get; set; } = LifecycleState.Spawning;
    public ITuiPlugin Plugin { get; }
    public string BasePath { get; }
    public CellBuffer FrontBuffer { get; private set; }
    public int WindowIndex { get; }

    public PluginInstance(ITuiPlugin plugin, Vom vom, int windowIndex)
    {
        Plugin = plugin;
        WindowIndex = windowIndex;
        BasePath = "";
        FrontBuffer = new CellBuffer();
    }
}