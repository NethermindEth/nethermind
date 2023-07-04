// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Analyzers
{
    public interface IDisconnectsAnalyzer
    {
        void ReportDisconnect(DisconnectReason reason, DisconnectType type, string details);
    }
}
