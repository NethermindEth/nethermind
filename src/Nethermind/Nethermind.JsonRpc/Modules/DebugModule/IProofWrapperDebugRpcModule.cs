// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.ProofAggregation;

namespace Nethermind.JsonRpc.Modules.DebugModule;

[RpcModule(ModuleType.Debug)]
public interface IProofWrapperDebugRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Returns recorded EIP-8288 / EIP-8437 proof wrapper lifecycle events after a sequence number, oldest first. " +
        "Recording is off unless `Network.ProofWrapperEventBufferSize` is positive.",
        EdgeCaseHint = "At most 2000 events per call; page with the returned `latest`. `missed` counts events the ring buffer overwrote.",
        IsImplemented = true,
        IsSharable = true)]
    ResultWrapper<ProofWrapperEventPage> debug_proofWrapperEvents(long? after = null, int? limit = null);
}
