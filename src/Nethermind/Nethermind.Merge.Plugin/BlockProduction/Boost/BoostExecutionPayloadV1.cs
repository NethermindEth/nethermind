// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data.V1;

namespace Nethermind.Merge.Plugin.BlockProduction.Boost;

public class BoostExecutionPayloadV1
{
    public ExecutionPayloadV1 Block { get; init; } = null!;
    public UInt256 Profit { get; init; }
}
