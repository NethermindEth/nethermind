// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Core.Domain
{
    public enum DataAvailability
    {
        Unknown,
        Available,
        SubscriptionEnded,
        ExpiryRuleExceeded,
        UnitsExceeded,
        DataDeliveryReceiptNotProvided,
        DataDeliveryReceiptInvalid
    }
}
