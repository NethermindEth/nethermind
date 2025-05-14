// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Optimism.CL;

namespace Nethermind.Optimism.Cl.Rpc;


/// <summary>
/// Represents a snapshot of the rollup driver.
/// </summary>
/// <remarks>
/// Spec: https://specs.optimism.io/protocol/rollup-node.html?utm_source=op-docs&utm_medium=docs#syncstatus
/// </remarks>
public sealed record OptimismSyncStatus
{
    // L1
    public required L1BlockRef CurrentL1 { get; init; }
    public required L1BlockRef CurrentL1Finalized { get; init; }
    public required L1BlockRef HeadL1 { get; init; }
    public required L1BlockRef SafeL1 { get; init; }
    public required L1BlockRef FinalizedL1 { get; init; }
    // L2
    public required L2BlockRef UnsafeL2 { get; init; }
    public required L2BlockRef SafeL2 { get; init; }
    public required L2BlockRef FinalizedL2 { get; init; }
    public required L2BlockRef PendingSafeL2 { get; init; }
    public required L2BlockRef QueuedUnsafeL2 { get; init; }
}
