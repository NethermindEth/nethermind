// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Ssz;

[assembly: SszPreset(typeof(Nethermind.BeaconChain.Spec.Presets), nameof(Nethermind.BeaconChain.Spec.Presets.IsGnosis))]

namespace Nethermind.BeaconChain.Spec;

/// <summary>Process-wide consensus preset, fixed before state types or codecs are used.</summary>
internal static class BeaconPresetSelection
{
    private static int s_selected;

    public static bool IsGnosis
    {
        get
        {
            int selected = Volatile.Read(ref s_selected);
            if (selected == 0) selected = Interlocked.CompareExchange(ref s_selected, 1, 0);
            return selected == 2;
        }
    }

    internal static void Initialize(ulong chainId)
    {
        bool gnosis = chainId is BlockchainIds.Gnosis or BlockchainIds.Chiado;
#if MINIMAL_PRESET
        if (gnosis) throw new InvalidOperationException("The minimal test preset cannot follow Gnosis or Chiado.");
#endif
        int selected = gnosis ? 2 : 1;
        int previous = Interlocked.CompareExchange(ref s_selected, selected, 0);
        if (previous != 0 && previous != selected)
            throw new InvalidOperationException("The beacon preset cannot change after consensus types have been used.");
    }
}
