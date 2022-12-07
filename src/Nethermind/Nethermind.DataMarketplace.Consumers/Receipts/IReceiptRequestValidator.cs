// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Receipts
{
    public interface IReceiptRequestValidator
    {
        bool IsValid(DataDeliveryReceiptRequest receiptRequest, long unpaidUnits, long consumedUnits,
            long purchasedUnits);
    }
}
