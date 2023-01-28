// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2
{
    public interface IEth1DataProvider
    {
        IAsyncEnumerable<Eth1Data> GetEth1DataDescendingAsync(ulong maximumTimestampInclusive, ulong minimumTimestampInclusive, CancellationToken cancellationToken);
        IAsyncEnumerable<Deposit> GetDepositsAsync(Bytes32 eth1BlockHash, ulong startIndex, ulong maximum, CancellationToken cancellationToken);
    }
}
