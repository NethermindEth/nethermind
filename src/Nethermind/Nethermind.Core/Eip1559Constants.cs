// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core
{
    public partial class Eip1559Constants
    {

        public static readonly UInt256 DefaultBaseFeeMaxChangeDenominator = 8;

        public static readonly ulong DefaultElasticityMultiplier = 2;
    }
}
