// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Repositories
{
    public interface IDepositDetailsRepository
    {
        Task<DepositDetails?> GetAsync(Keccak id);
        Task<PagedResult<DepositDetails>> BrowseAsync(GetDeposits query);
        Task AddAsync(DepositDetails deposit);
        Task UpdateAsync(DepositDetails deposit);
    }
}
