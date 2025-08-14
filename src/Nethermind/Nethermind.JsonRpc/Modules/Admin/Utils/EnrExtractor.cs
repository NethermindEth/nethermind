// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class EnrExtractor
    {
        public static string? GetEnrFromPeer(Peer peer)
        {
            // ENR strings require signed NodeRecord instances from the discovery layer
            // This would require access to IDiscoveryManager and proper ENR handshake
            // For now, return null as this matches the expected behavior
            return null;
        }
    }
}
