// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public class BoostPayloadAttributes : PayloadAttributes
{
    public long? GasLimit { get; set; }

    public override long GetGasLimit(BlockHeader parent, IGasLimitCalculator gasLimitCalculator)
        => GasLimit ?? base.GetGasLimit(parent, gasLimitCalculator);
}
