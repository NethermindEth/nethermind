// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Queries;

namespace Nethermind.DataMarketplace.Providers.Repositories
{
    public interface IProviderDepositApprovalRepository
    {
        Task<DepositApproval?> GetAsync(Keccak id);
        Task<PagedResult<DepositApproval>> BrowseAsync(GetProviderDepositApprovals query);
        Task AddAsync(DepositApproval depositApproval);
        Task UpdateAsync(DepositApproval depositApproval);
    }
}
