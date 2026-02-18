// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc;

internal class XdcBaseFeeCalculator : IBaseFeeCalculator
{
    public const long BaseFee = 12500000000;

    public UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559)
    {
        return BaseFee;
    }
}
