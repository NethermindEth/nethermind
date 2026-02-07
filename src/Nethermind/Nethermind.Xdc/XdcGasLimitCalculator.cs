// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc;

internal class XdcGasLimitCalculator : IGasLimitCalculator
{
    public long GetGasLimit(BlockHeader parentHeader)
    {
        return XdcConstants.TargetGasLimit;
    }
}
