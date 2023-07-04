// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public class Eip1559Constants
    {

        public static readonly UInt256 DefaultForkBaseFee = 1.GWei();

        public static readonly UInt256 DefaultBaseFeeMaxChangeDenominator = 8;

        public static readonly int DefaultElasticityMultiplier = 2;

        // The above values are the default ones. However, we're allowing to override it from genesis
        public static UInt256 ForkBaseFee { get; set; } = DefaultForkBaseFee;
        public static UInt256 BaseFeeMaxChangeDenominator { get; set; } = DefaultBaseFeeMaxChangeDenominator;
        public static long ElasticityMultiplier { get; set; } = DefaultElasticityMultiplier;
    }
}
