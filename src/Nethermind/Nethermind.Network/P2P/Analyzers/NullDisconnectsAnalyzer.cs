// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Analyzers
{
    public class NullDisconnectsAnalyzer : IDisconnectsAnalyzer
    {
        private NullDisconnectsAnalyzer() { }

        public static IDisconnectsAnalyzer Instance { get; } = new NullDisconnectsAnalyzer();

        public void ReportDisconnect(DisconnectReason reason, DisconnectType type, string details) { }
    }
}
