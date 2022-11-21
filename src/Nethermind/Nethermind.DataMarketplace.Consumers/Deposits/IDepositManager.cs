// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Deposits
{
    public interface IDepositManager
    {
        Task<DepositDetails?> GetAsync(Keccak depositId);
        Task<PagedResult<DepositDetails>> BrowseAsync(GetDeposits query);
        Task<Keccak?> MakeAsync(Keccak assetId, uint units, UInt256 value, Address address, UInt256? gasPrice = null);
    }
}
