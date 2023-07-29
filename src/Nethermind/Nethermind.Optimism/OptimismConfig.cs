// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism;

public class OptimismConfig : IOptimismConfig
{
    public bool Enabled { get; set; }
    public long RegolithBlockNumber { get; set; }
    public long BedrockBlockNumber { get; set; }
    public Address L1FeeReceiver { get ; set; } = Address.Zero;
}
