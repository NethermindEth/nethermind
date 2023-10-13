// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Facade.Filters;

namespace Nethermind.Blockchain.Filters
{
    public interface IFilterManager
    {
        FilterLog[] GetLogs(int filterId);
        FilterLog[] PollLogs(int filterId);
        Commitment[] GetBlocksHashes(int filterId);
        Commitment[] PollBlockHashes(int filterId);
        Commitment[] PollPendingTransactionHashes(int filterId);
    }
}
