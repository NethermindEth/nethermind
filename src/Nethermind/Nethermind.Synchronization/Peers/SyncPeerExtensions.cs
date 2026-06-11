// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.RegularExpressions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Synchronization.Peers
{
    public static partial class SyncPeerExtensions
    {
        // Check if OpenEthereum supports GetNodeData
        private static readonly Version _openEthereumSecondRemoveGetNodeDataVersion = new(3, 3, 3);
        private static readonly Version _openEthereumFirstRemoveGetNodeDataVersion = new(3, 1, 0);

        public static bool SupportsAllocation(this PeerInfo peerInfo, AllocationContexts contexts)
        {
            if (contexts == AllocationContexts.BlockAccessLists && !peerInfo.SyncPeer.SupportsBlockAccessLists())
            {
                return false;
            }

            // check if OpenEthereum supports state sync
            if ((contexts & AllocationContexts.State) != 0 // only for State allocations
                && peerInfo.SyncPeer.ClientType == NodeClientType.OpenEthereum) // only for OE
            {
                // try get OpenEthereum version
                Version? openEthereumVersion = peerInfo.SyncPeer.GetOpenEthereumVersion(out _);
                if (openEthereumVersion is not null)
                {
                    int versionComparison = openEthereumVersion.CompareTo(_openEthereumSecondRemoveGetNodeDataVersion);
                    return versionComparison >= 0 || openEthereumVersion < _openEthereumFirstRemoveGetNodeDataVersion;
                }
            }

            return true;
        }

        public static bool SupportsBlockAccessLists(this ISyncPeer peer) => peer.ProtocolVersion >= EthVersions.Eth71;

        private static readonly Regex _openEthereumVersionRegex = OpenEthereumRegex();

        public static Version? GetOpenEthereumVersion(this ISyncPeer peer, out int releaseCandidate)
        {
            releaseCandidate = 0;

            if (peer.ClientType == NodeClientType.OpenEthereum)
            {
                if (peer.ClientId is null)
                    return null;

                Match match = _openEthereumVersionRegex.Match(peer.ClientId);

                if (match.Success && Version.TryParse(match.Groups["mainVersion"].Value, out Version? version))
                {
                    int.TryParse(match.Groups["rc"].Value, out releaseCandidate);
                    return version;
                }
            }

            return null;
        }

        [GeneratedRegex("OpenEthereum\\/([a-zA-z-0-9]*\\/)*v(?<version>(?<mainVersion>[0-9]\\.[0-9]\\.[0-9])-?(rc\\.(?<rc>[0-9]*))?)")]
        private static partial Regex OpenEthereumRegex();
    }
}
