// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;

namespace Nethermind.Facade.Simulate;

public class SimulateRequestState : IBlobBaseFeeOverrideProvider
{
    public bool Validate { get; set; }
    public UInt256? BlobBaseFeeOverride { get; set; }
    public ulong TotalGasLeft { get; set; }
    public ulong BlockGasLeft { get; set; }

    /// <summary>Remaining EIP-8037 state-dimension block budget, mirroring <see cref="BlockGasLeft"/> (execution).</summary>
    public ulong BlockStateGasLeft { get; set; }

    /// <summary>The calls of the block being simulated, in transaction order.</summary>
    public TransactionWithSourceDetails[] Calls { get; set; } = [];
}
