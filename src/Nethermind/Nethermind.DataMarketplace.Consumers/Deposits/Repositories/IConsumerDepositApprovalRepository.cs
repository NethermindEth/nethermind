// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Repositories
{
    public interface IConsumerDepositApprovalRepository
    {
        Task<DepositApproval?> GetAsync(Keccak id);
        Task<PagedResult<DepositApproval>> BrowseAsync(GetConsumerDepositApprovals query);
        Task AddAsync(DepositApproval depositApproval);
        Task UpdateAsync(DepositApproval depositApproval);
    }
}
