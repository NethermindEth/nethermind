// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DataAssetRules
    {
        public DataAssetRule Expiry { get; }
        public DataAssetRule? UpfrontPayment { get; }

        public DataAssetRules(DataAssetRule expiry, DataAssetRule? upfrontPayment = null)
        {
            if (expiry is null || expiry.Value == 0)
            {
                throw new ArgumentException($"Invalid data asset expiry rule value: {expiry?.Value}.",
                    nameof(expiry));
            }

            Expiry = expiry;
            UpfrontPayment = upfrontPayment;
        }
    }
}
