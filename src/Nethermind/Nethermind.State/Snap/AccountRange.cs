// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class AccountRange
    {
        public AccountRange(ValueKeccak rootHash, ValueKeccak startingHash, ValueKeccak? limitHash = null, long? blockNumber = null)
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
        public ValueKeccak RootHash { get; }

        /// <summary>
        /// Account hash of the first to retrieve
        /// </summary>
        public ValueKeccak StartingHash { get; }

        /// <summary>
        /// Account hash after which to stop serving data
        /// </summary>
        public ValueKeccak? LimitHash { get; }

        public override string ToString()
        {
            return $"AccountRange: ({BlockNumber}, {RootHash}, {StartingHash}, {LimitHash})";
        }
    }
}
