// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.CL.Derivation;

namespace Nethermind.Optimism.CL;

public interface IExecutionEngineManager
{
    Task Initialize();
    Task<(BlockId Head, BlockId Finalized, BlockId Safe)> GetCurrentBlocks();
    Task<BlockId?> ProcessNewDerivedPayloadAttributes(PayloadAttributesRef payloadAttributes, CancellationToken token);
    Task<bool> FinalizeBlock(BlockId finalizedBlock, CancellationToken token);
    Task<P2PPayloadStatus> ProcessNewP2PExecutionPayload(ExecutionPayloadV3 executionPayloadV3, CancellationToken token);
    Task OnELSynced { get; }
}

public enum P2PPayloadStatus
{
    Valid,
    Invalid,
    Syncing
}
