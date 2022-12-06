// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class DataAssetRulesForRpc
    {
        public DataAssetRuleForRpc? Expiry { get; set; }
        public DataAssetRuleForRpc? UpfrontPayment { get; set; }

        public DataAssetRulesForRpc()
        {
        }

        public DataAssetRulesForRpc(DataAssetRules rules)
        {
            Expiry = new DataAssetRuleForRpc(rules.Expiry);
            UpfrontPayment = rules.UpfrontPayment is null ? null : new DataAssetRuleForRpc(rules.UpfrontPayment);
        }
    }
}
