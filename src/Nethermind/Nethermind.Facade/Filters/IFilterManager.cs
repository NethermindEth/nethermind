// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Facade.Filters;

namespace Nethermind.Blockchain.Filters
{
    public interface IFilterManager
    {
        FilterLog[] PollLogs(int filterId);
        Hash256[] PollBlockHashes(int filterId);
        Hash256[] PollPendingTransactionHashes(int filterId);
    }
}
