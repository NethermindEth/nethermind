// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Repositories
{
    public interface IEthRequestRepository
    {
        Task<EthRequest?> GetLatestAsync(string host);
        Task AddAsync(EthRequest request);
        Task UpdateAsync(EthRequest request);
        Task<UInt256> SumDailyRequestsTotalValueAsync(DateTime date);
    }
}
