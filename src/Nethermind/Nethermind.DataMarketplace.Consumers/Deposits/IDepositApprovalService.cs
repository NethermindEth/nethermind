// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Deposits
{
    public interface IDepositApprovalService
    {
        Task<PagedResult<DepositApproval>> BrowseAsync(GetConsumerDepositApprovals query);
        Task<Keccak?> RequestAsync(Keccak assetId, Address consumer, string kyc);
        Task ConfirmAsync(Keccak assetId, Address consumer);
        Task RejectAsync(Keccak assetId, Address consumer);
        Task UpdateAsync(IReadOnlyList<DepositApproval> approvals, Address provider);
    }
}
