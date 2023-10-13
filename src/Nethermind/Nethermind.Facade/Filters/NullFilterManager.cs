// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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

        public Commitment[] GetBlocksHashes(int filterId)
        {
            return Array.Empty<Commitment>();
        }

        public Commitment[] PollBlockHashes(int filterId)
        {
            return Array.Empty<Commitment>();
        }

        public Commitment[] PollPendingTransactionHashes(int filterId)
        {
            return Array.Empty<Commitment>();
        }
    }
}
