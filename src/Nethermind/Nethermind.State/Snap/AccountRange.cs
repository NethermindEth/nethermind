// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class AccountRange
    {
        public AccountRange(ValueHash256 rootHash, ValueHash256 startingHash, ValueHash256? limitHash = null, long? blockNumber = null)
        {
            RootHash = rootHash;
            StartingHash = startingHash;
            BlockNumber = blockNumber;
            LimitHash = limitHash;
        }

        public long? BlockNumber { get; }

        /// <summary>
        /// Root hash of the account trie to serve
        /// </summary>
        public ValueHash256 RootHash { get; }

        /// <summary>
        /// Account hash of the first to retrieve
        /// </summary>
        public ValueHash256 StartingHash { get; }

        /// <summary>
        /// Account hash after which to stop serving data
        /// </summary>
        public ValueHash256? LimitHash { get; }

        public override string ToString()
        {
            return $"AccountRange: ({BlockNumber}, {RootHash}, {StartingHash}, {LimitHash})";
        }
    }
}
