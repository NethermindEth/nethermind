// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Facade.Filters;

namespace Nethermind.Blockchain.Filters
{
    public interface IFilterManager
    {
        FilterLog[] GetLogs(int filterId);
        FilterLog[] PollLogs(int filterId);
        Keccak[] GetBlocksHashes(int filterId);
        Keccak[] PollBlockHashes(int filterId);
        Keccak[] PollPendingTransactionHashes(int filterId);
    }
}
