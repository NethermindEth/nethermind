// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Filters;

namespace Nethermind.Blockchain.Filters
{
    public class NullFilterManager : IFilterManager
    {
        public static NullFilterManager Instance { get; } = new();

        private NullFilterManager()
        {
        }

        public FilterLog[] GetLogs(int filterId)
        {
            return Array.Empty<FilterLog>();
        }

        public FilterLog[] PollLogs(int filterId)
        {
            return Array.Empty<FilterLog>();
        }

        public Keccak[] GetBlocksHashes(int filterId)
        {
            return Array.Empty<Keccak>();
        }

        public Keccak[] PollBlockHashes(int filterId)
        {
            return Array.Empty<Keccak>();
        }

        public Keccak[] PollPendingTransactionHashes(int filterId)
        {
            return Array.Empty<Keccak>();
        }
    }
}
