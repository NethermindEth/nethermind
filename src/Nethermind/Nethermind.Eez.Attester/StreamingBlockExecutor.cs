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
using Nethermind.Eez.Prove;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingBlockExecutor(ISpecProvider specProvider, EezChainRules rules, ILogManager logManager)
{
    private readonly EezStatelessExecutor _executor = new(specProvider, rules, logManager);

    internal ExecutedStreamingBlock Execute(SessionOperation operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        BlockWitness block = operation.Block ?? throw new InvalidOperationException("not a block operation");
        using Witness witness = WindowAssembler.ToWitness(block.Witness);
        EezStatelessBlockResult result;
        try
        {
            result = _executor.Execute([new EezStatelessBlock(block.Rlp.ToByteArray(), witness)], [])[0];
        }
        catch (EezStatelessException e)
        {
            throw new RpcException(new Status(e.Failure == EezStatelessFailure.Rejected ? StatusCode.FailedPrecondition : StatusCode.Internal,
                e.Failure == EezStatelessFailure.Rejected ? "block validation rejected" : "execution backend invariant failed"));
        }
        catch (Exception e) when (e is WitnessValidationException or RlpException or MissingTrieNodeException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "invalid execution witness"));
        }

        cancellation.ThrowIfCancellationRequested();
        ExecutedStreamingBlock executed = new(result.Block.Number, ByteString.CopyFrom(result.Hash.Bytes),
            ByteString.CopyFrom(result.Parent.Hash!.Bytes), ByteString.CopyFrom(result.PreStateRoot.Bytes), ByteString.CopyFrom(result.PostStateRoot.Bytes));
        if (executed.Number != block.Number || executed.Hash != block.Hash || executed.ParentHash != block.ParentHash ||
            executed.PreStateRoot != operation.PreStateRoot || !Rlp.Encode(result.Block).Bytes.AsSpan().SequenceEqual(block.Rlp.Span))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "executed block differs from admitted block"));
        }

        return executed;
    }
}
