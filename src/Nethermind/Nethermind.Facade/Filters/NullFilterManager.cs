// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            return [];
        }

        public FilterLog[] PollLogs(int filterId)
        {
            return [];
        }

        public Hash256[] GetBlocksHashes(int filterId)
        {
            return [];
        }

        public Hash256[] PollBlockHashes(int filterId)
        {
            return [];
        }

        public Hash256[] PollPendingTransactionHashes(int filterId)
        {
            return [];
        }
    }
}
