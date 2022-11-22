// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Core.Repositories
{
    public interface IReceiptRepository
    {
        Task<DataDeliveryReceiptDetails?> GetAsync(Keccak id);

        Task<IReadOnlyList<DataDeliveryReceiptDetails>> BrowseAsync(Keccak? depositId = null, Keccak? dataAssetId = null, Keccak? sessionId = null);

        Task AddAsync(DataDeliveryReceiptDetails receipt);
        Task UpdateAsync(DataDeliveryReceiptDetails receipt);
    }
}
