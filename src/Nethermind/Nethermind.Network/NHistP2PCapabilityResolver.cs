// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public class NHistP2PCapabilityResolver(ISyncConfig syncConfig, IFlatDbConfig flatDbConfig) : IP2PCapabilityResolver
{
    private static readonly Capability NHistCapability = new(Protocol.NHist, NHistVersions.NHist1);

    public event Action? Changed { add { } remove { } }

    public void Resolve(ISet<Capability> capabilities)
    {
        // The protocol only activates when both HELLOs carry the capability, so consumers
        // (clone and windowed-backfill clients) must advertise it too, not only servers.
        bool consumes = flatDbConfig.HistoryArchiveCloneEnabled
            || (flatDbConfig.HistoryEnabled && flatDbConfig.HistoryRetentionBlocks > 0);
        if (syncConfig.HistoryServingEnabled == true || consumes)
        {
            capabilities.Add(NHistCapability);
        }
    }
}
