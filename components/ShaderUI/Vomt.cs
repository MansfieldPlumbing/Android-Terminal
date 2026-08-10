using System.Collections.Generic;

namespace Subsystem;

public class Vomt
{
    public required string PluginName { get; init; }
    public required List<(string KeySuffix, object DefaultValue)> Entries { get; init; }
}
