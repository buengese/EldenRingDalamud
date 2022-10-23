using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Network;
using Dalamud.IoC;
using Dalamud.Plugin;
using Condition = Dalamud.Game.ClientState.Conditions.Condition;

namespace EldenRing;

internal class Service
{
    /// <summary>
    /// Gets the Dalamud plugin interface.
    /// </summary>
    [PluginService]
    internal static DalamudPluginInterface Interface { get; private set; } = null!;

    /// <summary>
    /// Gets the Dalamud client state.
    /// </summary>
    [PluginService]
    internal static ClientState ClientState { get; private set; } = null!;

    /// <summary>
    /// Gets the Dalamud command manager.
    /// </summary>
    [PluginService]
    internal static CommandManager CommandManager { get; private set; } = null!;


    /// <summary>
    /// Gets the Dalamud object table.
    /// </summary>
    [PluginService]
    internal static ObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    internal static DataManager DataManager { get; private set; } = null;

    [PluginService]
    internal static Framework Framework { get; private set; } = null;

    [PluginService]
    internal static ChatGui ChatGui { get; private set; } = null;

    [PluginService]
    internal static GameNetwork GameNetwork { get; private set; } = null;

    [PluginService]
    internal static Condition Condition { get; private set; } = null;

    [PluginService]
    internal static SigScanner SigScanner { get; private set; } = null;
}
