// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class DataAssetRuleForRpc
    {
        public UInt256 Value { get; set; }

        public DataAssetRuleForRpc()
        {
        }

        public DataAssetRuleForRpc(DataAssetRule rule)
        {
            Value = rule.Value;
        }
    }
}
