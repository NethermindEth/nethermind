// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Integration.Test.JsonRpc.Dto
{
    public class DataAssetRulesDto
    {
        public DataAssetRuleDto Expiry { get; set; }
        public DataAssetRuleDto UpfrontPayment { get; set; }
    }
}
