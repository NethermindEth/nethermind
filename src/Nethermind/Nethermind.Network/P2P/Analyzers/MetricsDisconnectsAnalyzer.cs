// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Analyzers
{
    public class MetricsDisconnectsAnalyzer : IDisconnectsAnalyzer
    {
        public void ReportDisconnect(DisconnectReason reason, DisconnectType type, string details)
        {
            if (type == DisconnectType.Remote)
            {
                Metrics.RemoteDisconnectsTotal.Increment(reason);
            }

            if (type == DisconnectType.Local)
            {
                Metrics.LocalDisconnectsTotal.Increment(reason);
            }
        }
    }
}
