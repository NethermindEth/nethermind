// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.Enr;
using Nethermind.State.SnapServer;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Decides which snap versions the local ENR advertises through <see cref="SnapEntry"/>.
/// </summary>
/// <remarks>
/// Unlike Hello, which also offers snap for snap sync alone, the entry needs snap serving;
/// a server that cannot serve (hash db layout) disconnects requests.
/// </remarks>
public sealed class SnapEnrAdvertisement(ISyncConfig syncConfig, ISnapServer snapServer, IP2PCapabilityResolver[] capabilityResolvers)
{
    private static readonly Capability Snap2Capability = new(Protocol.Snap, SnapVersions.Snap2);

    /// <summary>Raised when the advertised snap versions may have changed.</summary>
    public event Action? Changed
    {
        add
        {
            foreach (IP2PCapabilityResolver resolver in capabilityResolvers) resolver.Changed += value;
        }
        remove
        {
            foreach (IP2PCapabilityResolver resolver in capabilityResolvers) resolver.Changed -= value;
        }
    }

    public bool ServesSnap => syncConfig.SnapServingEnabled == true && snapServer.CanServe;

    /// <summary>Whether snap/2 is served on top of snap/1; implies <see cref="ServesSnap"/>.</summary>
    public bool ServesSnap2
    {
        get
        {
            if (!ServesSnap) return false;

            HashSet<Capability> capabilities = [];
            foreach (IP2PCapabilityResolver resolver in capabilityResolvers)
            {
                resolver.Resolve(capabilities);
            }

            return capabilities.Contains(Snap2Capability);
        }
    }
}
