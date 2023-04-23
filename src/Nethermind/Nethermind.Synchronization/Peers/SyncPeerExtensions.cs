// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.RegularExpressions;
using Nethermind.Blockchain.Synchronization;
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
            // check if OpenEthereum supports state sync
            if ((contexts & AllocationContexts.State) != 0 // only for State allocations
                && peerInfo.SyncPeer.ClientType == NodeClientType.OpenEthereum) // only for OE
            {
                // try get OpenEthereum version
                Version? openEthereumVersion = peerInfo.SyncPeer.GetOpenEthereumVersion(out _);
                if (openEthereumVersion is not null)
                {
                    int versionComparision = openEthereumVersion.CompareTo(_openEthereumSecondRemoveGetNodeDataVersion);
                    return versionComparision >= 0 || openEthereumVersion < _openEthereumFirstRemoveGetNodeDataVersion;
                }
            }

            // check if peer supports snap sync
            if ((contexts & AllocationContexts.Snap) != 0)
            {
                // TODO: Remove when Nethermind implements snap server
                return peerInfo.SyncPeer.ClientType != NodeClientType.Nethermind;
            }

            return true;
        }

        private static readonly Regex _openEthereumVersionRegex = OpenEthereumRegex();

        public static Version? GetOpenEthereumVersion(this ISyncPeer peer, out int releaseCandidate)
        {
            if (peer.ClientType == NodeClientType.OpenEthereum)
            {
                Match match = _openEthereumVersionRegex.Match(peer.ClientId);

                if (match.Success && Version.TryParse(match.Groups["mainVersion"].Value, out Version version))
                {
                    int.TryParse(match.Groups["rc"].Value, out releaseCandidate);
                    return version;
                }
            }

            releaseCandidate = 0;
            return null;
        }

        [GeneratedRegex("OpenEthereum\\/([a-zA-z-0-9]*\\/)*v(?<version>(?<mainVersion>[0-9]\\.[0-9]\\.[0-9])-?(rc\\.(?<rc>[0-9]*))?)")]
        private static partial Regex OpenEthereumRegex();
    }
}
