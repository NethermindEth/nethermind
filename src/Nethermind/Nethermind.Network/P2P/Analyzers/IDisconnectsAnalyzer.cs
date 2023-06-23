// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Analyzers
{
    public interface IDisconnectsAnalyzer
    {
        void ReportDisconnect(EthDisconnectReason reason, DisconnectType type, string details);
    }
}
