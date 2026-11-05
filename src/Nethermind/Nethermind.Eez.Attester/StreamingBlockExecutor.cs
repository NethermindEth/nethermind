// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Prove;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingBlockExecutor
{
    private readonly EezStatelessExecutor? _executor;
    private readonly NativeStreamingProfile? _nativeProfile;
    private readonly ILogManager _logs;

    internal StreamingBlockExecutor(ISpecProvider specProvider, EezChainRules rules, ILogManager logManager)
    {
        _executor = new EezStatelessExecutor(specProvider, rules, logManager);
        _logs = logManager;
    }

    internal StreamingBlockExecutor(NativeStreamingProfile nativeProfile, ILogManager logs) => (_nativeProfile, _logs) = (nativeProfile, logs);

    internal ExecutedStreamingBlock Execute(SessionOperation operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_nativeProfile, operation.NativeProfile))
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "session and execution profiles differ"));
        if (operation.ReusedBlock is { } reused) return reused;
        BlockWitness block = operation.Block ?? throw new InvalidOperationException("not a block operation");
        EezStatelessBlockResult result;
        NativeStreamingEvidence? history = null;
        try
        {
            EezStatelessExecutor executor;
            if (_nativeProfile is not null)
            {
                history = (operation.NativeHistory ?? throw new InvalidOperationException("native L1 checkpoint is missing"))
                    .Stage(operation.NativeHistoryLength, block.Witness, _nativeProfile.Headers);
                executor = history.CreateExecutor(_nativeProfile.Genesis, _logs);
            }
            else executor = _executor!;
            using Witness witness = _nativeProfile is null ? WindowAssembler.ToWitness(block.Witness) : WindowAssembler.ToL2Witness(block.Witness);
            result = executor.Execute([new EezStatelessBlock(block.Rlp.ToByteArray(), witness)], [])[0];
        }
        catch (EezStatelessException e)
        {
            throw new RpcException(new Status(e.Failure == EezStatelessFailure.Rejected ? StatusCode.FailedPrecondition : StatusCode.Internal,
                e.Failure == EezStatelessFailure.Rejected ? "block validation rejected" : "execution backend invariant failed"));
        }
        catch (Exception e) when (e is WitnessValidationException or RlpException or MissingTrieNodeException or EezSettlementException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "invalid execution witness"));
        }

        cancellation.ThrowIfCancellationRequested();
        ExecutedStreamingBlock executed = new(result.Block.Number, ByteString.CopyFrom(result.Hash.Bytes),
            ByteString.CopyFrom(result.Parent.Hash!.Bytes), ByteString.CopyFrom(result.PreStateRoot.Bytes), ByteString.CopyFrom(result.PostStateRoot.Bytes), history);
        if (executed.Number != block.Number || executed.Hash != block.Hash || executed.ParentHash != block.ParentHash ||
            executed.PreStateRoot != operation.PreStateRoot || !Rlp.Encode(result.Block).Bytes.AsSpan().SequenceEqual(block.Rlp.Span))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "executed block differs from admitted block"));
        }

        return executed;
    }
}
