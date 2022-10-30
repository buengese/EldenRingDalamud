using System;

using Dalamud.Game;
using Dalamud.Logging;

namespace EldenRing;

/// <summary>
/// Plugin address resolver.
/// </summary>
internal class PluginAddressResolver : BaseAddressResolver
{

    /// <summary>
    /// Gets the address of fpIsIconReplacable.
    /// </summary>
    public IntPtr SetGlobalBGM { get; private set; }
    
    public IntPtr ActionIntegrity { get; private set; }

    /// <inheritdoc/>
    protected override void Setup64Bit(SigScanner scanner)
    {
        this.SetGlobalBGM = scanner.ScanText("4C 8B 15 ?? ?? ?? ?? 4D 85 D2 74 58");
        this.ActionIntegrity = scanner.ScanText("48 8B C4 44 88 40 18 89 48 08");

        PluginLog.Verbose("===== EldenRingPlugin =====");
        PluginLog.Verbose($"{nameof(this.SetGlobalBGM)}    0x{this.SetGlobalBGM:X}");
        PluginLog.Verbose($"{nameof(this.ActionIntegrity)} 0x{this.ActionIntegrity:X}");
    }
}