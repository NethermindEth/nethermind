// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public class BoostPayloadAttributes : PayloadAttributes
{
    public ulong? GasLimit { get; set; }

    public override ulong? GetGasLimit() => GasLimit;
}
