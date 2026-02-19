// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc;

internal class XdcDifficultyCalculator : IDifficultyCalculator
{
    public UInt256 Calculate(BlockHeader header, BlockHeader parent)
    {
        return UInt256.One;
    }
}
