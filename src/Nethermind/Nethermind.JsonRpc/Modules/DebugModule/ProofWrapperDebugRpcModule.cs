// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.ProofAggregation;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class ProofWrapperDebugRpcModule(ProofWrapperEventFeed events) : IProofWrapperDebugRpcModule
{
    private const int DefaultLimit = 1000;

    public ResultWrapper<ProofWrapperEventPage> debug_proofWrapperEvents(long? after = null, int? limit = null) =>
        events.IsEnabled
            ? ResultWrapper<ProofWrapperEventPage>.Success(events.Read(after ?? 0, limit ?? DefaultLimit))
            : ResultWrapper<ProofWrapperEventPage>.Fail("Proof wrapper event recording is disabled; set Network.ProofWrapperEventBufferSize.",
                ErrorCodes.MethodNotFound);
}
