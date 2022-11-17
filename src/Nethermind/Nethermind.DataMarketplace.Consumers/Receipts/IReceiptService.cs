// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Receipts
{
    public interface IReceiptService
    {
        Task SendAsync(DataDeliveryReceiptRequest request, int fetchSessionRetries = 3,
            int fetchSessionRetryDelayMilliseconds = 3000);
    }
}
