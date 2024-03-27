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

        public IFilterLog[] GetLogs(int filterId)
        {
            return Array.Empty<IFilterLog>();
        }

        public IFilterLog[] PollLogs(int filterId)
        {
            return Array.Empty<IFilterLog>();
        }

        public Hash256[] GetBlocksHashes(int filterId)
        {
            return Array.Empty<Hash256>();
        }

        public Hash256[] PollBlockHashes(int filterId)
        {
            return Array.Empty<Hash256>();
        }

        public Hash256[] PollPendingTransactionHashes(int filterId)
        {
            return Array.Empty<Hash256>();
        }
    }
}
