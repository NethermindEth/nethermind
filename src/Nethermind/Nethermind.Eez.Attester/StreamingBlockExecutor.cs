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
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Prove;
using Nethermind.Logging;
using Nethermind.Serialization.FluentRlp;
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
        catch (Exception e) when (e is WitnessValidationException or RlpReaderException or MissingTrieNodeException or EezSettlementException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "invalid execution witness"));
        }

        cancellation.ThrowIfCancellationRequested();
        if (result.Block.Number != block.Number || !result.Hash.Bytes.SequenceEqual(block.Hash.Span) ||
            !result.Parent.Hash!.Bytes.SequenceEqual(block.ParentHash.Span) ||
            !result.PreStateRoot.Bytes.SequenceEqual(operation.PreStateRoot.Span) ||
            !Rlp.Encode(result.Block).AsSpan().SequenceEqual(block.Rlp.Span))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "executed block differs from admitted block"));
        }

        return new ExecutedStreamingBlock(block.Number, block.Hash, block.ParentHash, operation.PreStateRoot,
            ByteString.CopyFrom(result.PostStateRoot.Bytes), history)
        {
            Timestamp = result.Block.Timestamp,
            NativeBlock = history is null ? null : ValidatedRollupXBlock.Capture(result, block.Rlp.Memory, _nativeProfile!.Genesis.Rules,
                history.Count, history.Count == 0 ? default : history.Endpoint.Hash),
        };
    }
}
