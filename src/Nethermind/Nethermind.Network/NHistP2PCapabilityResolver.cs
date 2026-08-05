// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public class NHistP2PCapabilityResolver(ISyncConfig syncConfig) : IP2PCapabilityResolver
{
    private static readonly Capability NHistCapability = new(Protocol.NHist, NHistVersions.NHist1);

    public event Action? Changed { add { } remove { } }

    public void Resolve(ISet<Capability> capabilities)
    {
        if (syncConfig.HistoryServingEnabled == true)
        {
            capabilities.Add(NHistCapability);
        }
    }
}
