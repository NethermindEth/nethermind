// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Peers;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public interface IReceiptProcessor
    {
        Task<bool> TryProcessAsync(ProviderSession session, Address consumer, INdmProviderPeer peer,
            DataDeliveryReceiptRequest receiptRequest, DataDeliveryReceipt deliveryReceipt);
    }
}
