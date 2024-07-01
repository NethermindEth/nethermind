// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Core;

namespace Nethermind.Taiko;

/// This class can be refactored together with OptimismGasLimitCalculator into a common GasLimitCalculator
internal class TaikoGasLimitCalculator : IGasLimitCalculator
{
    public long GetGasLimit(BlockHeader parentHeader) =>
        throw new InvalidOperationException("GasLimit in Taiko should not be derived from parent header.");
}
