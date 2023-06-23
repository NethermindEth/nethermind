// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Analyzers
{
    public class NullDisconnectsAnalyzer : IDisconnectsAnalyzer
    {
        private NullDisconnectsAnalyzer() { }

        public static IDisconnectsAnalyzer Instance { get; } = new NullDisconnectsAnalyzer();

        public void ReportDisconnect(EthDisconnectReason reason, DisconnectType type, string details) { }
    }
}
