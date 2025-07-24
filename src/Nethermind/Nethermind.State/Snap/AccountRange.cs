// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class AccountRange(
        Hash256 rootHash,
        ValueHash256 startingHash,
        ValueHash256? limitHash = null,
        long? blockNumber = null)
    {
        public long? BlockNumber { get; } = blockNumber;

        /// <summary>
        /// Root hash of the account trie to serve
        /// </summary>
        public Hash256 RootHash { get; } = rootHash;

        /// <summary>
        /// Account hash of the first to retrieve
        /// </summary>
        public ValueHash256 StartingHash { get; } = startingHash;

        /// <summary>
        /// Account hash after which to stop serving data
        /// </summary>
        public ValueHash256? LimitHash { get; } = limitHash;

        public override string ToString()
        {
            return $"AccountRange: ({BlockNumber}, {RootHash}, {StartingHash}, {LimitHash})";
        }
    }
}
