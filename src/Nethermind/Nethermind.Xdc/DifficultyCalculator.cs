// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal class DifficultyCalculator : IDifficultyCalculator
{
    public UInt256 Calculate(BlockHeader header, BlockHeader parent)
    {
        return XdcConstants.DifficultyDefault;
    }
}
