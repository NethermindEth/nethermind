// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Attester;

internal sealed partial class ProverService
{
    private readonly object _preparationLock = new();
    private TaskCompletionSource? _preparationIdle;

    public override async Task<PrepareResponse> PrepareOrdinary(IAsyncStreamReader<PrepareChunk> requestStream, ServerCallContext context)
    {
        if (_native is null) return await base.PrepareOrdinary(requestStream, context);
        lock (_preparationLock)
        {
            if (_preparationIdle is not null) throw new RpcException(new Status(StatusCode.Unavailable, "another preparation is active"));
            _preparationIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        cancellation.CancelAfter(_options.RequestTimeout);
        CancellationToken token = cancellation.Token;
        Task<PrepareResponse> work = Prepare(requestStream, token);
        _ = DrainPreparation(work, cancellation);
        try { return await work.WaitAsync(token); }
        catch (OperationCanceledException)
        {
            throw new RpcException(context.CancellationToken.IsCancellationRequested
                ? new Status(StatusCode.Cancelled, "preparation cancelled")
                : new Status(StatusCode.DeadlineExceeded, "preparation deadline exceeded"));
        }
    }

    public override Task<RetainOrdinaryRangesResponse> RetainOrdinaryRanges(RetainOrdinaryRangesRequest request, ServerCallContext context)
    {
        if (_native is null) return base.RetainOrdinaryRanges(request, context);
        if (request.CalculateSize() > _options.Limits.MaxBytes)
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "retention request exceeds byte limit"));
        return Task.FromResult(_native.Cache.Retain(request, context.CancellationToken));
    }

    private async Task<PrepareResponse> Prepare(IAsyncStreamReader<PrepareChunk> stream, CancellationToken cancellation)
    {
        try
        {
            PrepareChunk first = await Next(stream, cancellation, cancellation, "PrepareOrdinary")
                ?? throw WindowAssembler.Invalid("empty preparation stream");
            long bytes = first.CalculateSize();
            if (bytes > _options.Limits.MaxBytes) throw PreparationQuota();
            if (first.KindCase != PrepareChunk.KindOneofCase.Header) throw WindowAssembler.Invalid("preparation header must be first");
            PrepareHeader header = first.Header.Clone();
            if (header.ExpectedValidationIdentity.Length != 32 || header.LeaseId.Length is not (0 or 32))
                throw WindowAssembler.Invalid("preparation identity or lease length invalid");
            if (header.ExpectedValidationIdentity != _native!.Identity || header.RollupId != _rollupId)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "preparation identity rejected"));
            if (header.FromBlock == 0 || header.ToBlock < header.FromBlock ||
                header.ToBlock - header.FromBlock >= (ulong)Math.Min(64, _options.Limits.MaxBlocks))
                throw WindowAssembler.Invalid("preparation bounds invalid");
            int count = (int)(header.ToBlock - header.FromBlock + 1);
            List<PreparedOrdinaryBlock> facts = new(count);
            long witnessItems = 0;
            long retainedBytes = 0;
            while (await Next(stream, cancellation, cancellation, "PrepareOrdinary") is { } chunk)
            {
                cancellation.ThrowIfCancellationRequested();
                bytes = checked(bytes + chunk.CalculateSize());
                if (bytes > _options.Limits.MaxBytes) throw PreparationQuota();
                if (chunk.KindCase != PrepareChunk.KindOneofCase.Block || facts.Count == count)
                    throw WindowAssembler.Invalid("preparation carries an unexpected chunk");
                BlockWitness input = chunk.Block;
                if (input.Number != header.FromBlock + (ulong)facts.Count || input.Hash.Length != 32 || input.ParentHash.Length != 32 || input.Witness is null)
                    throw WindowAssembler.Invalid("preparation block number, hashes or witness invalid");
                ExecutionWitness witness = input.Witness;
                witnessItems += (long)witness.State.Count + witness.Codes.Count + witness.Keys.Count + witness.Headers.Count;
                if (witnessItems > _options.Limits.MaxWitnessItems) throw PreparationQuota();
                EezStatelessBlock block = new(input.Rlp.ToByteArray(), WindowAssembler.ToWitness(witness));
                PreparedOrdinaryBlock fact;
                try
                {
                    using BackendAdmission.BackendLease slot = await _admission.Acquire(cancellation).ConfigureAwait(false);
                    fact = await _native.Prepare(block, cancellation).ConfigureAwait(false);
                }
                finally { block.Witness.Dispose(); }
                cancellation.ThrowIfCancellationRequested();
                if (fact.Number != input.Number || fact.Hash != new Hash256(input.Hash.Span) || fact.ParentHash != new Hash256(input.ParentHash.Span) ||
                    facts.Count != 0 && (fact.ParentHash != facts[^1].Hash || fact.PreStateRoot != facts[^1].PostStateRoot))
                    throw WindowAssembler.Invalid("prepared execution differs from the claimed range");
                retainedBytes = checked(retainedBytes + fact.RetainedBytes);
                if (retainedBytes > _options.Limits.MaxBytes) throw PreparationQuota();
                facts.Add(fact);
            }
            cancellation.ThrowIfCancellationRequested();
            if (facts.Count != count) throw WindowAssembler.Invalid("preparation ended before its declared range");
            ByteString lease = _native.Cache.Publish(facts, header.LeaseId, cancellation);
            return new PrepareResponse
            {
                ValidationIdentity = _native.Identity,
                FirstNumber = facts[0].Number,
                FirstHash = ByteString.CopyFrom(facts[0].Hash.Bytes),
                LastNumber = facts[^1].Number,
                LastHash = ByteString.CopyFrom(facts[^1].Hash.Bytes),
                PreparedCount = (ulong)facts.Count,
                LeaseId = lease,
            };
        }
        catch (Exception exception) when (exception is EezSettlementException or EezStatelessException or WitnessValidationException or RlpException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "ordinary preparation validation rejected"));
        }
    }

    private async Task DrainPreparation(Task work, CancellationTokenSource cancellation)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception exception) { _logger.LogDebug(exception, "ordinary preparation stopped"); }
        finally
        {
            cancellation.Dispose();
            lock (_preparationLock)
            {
                TaskCompletionSource idle = _preparationIdle!;
                _preparationIdle = null;
                idle.SetResult();
            }
        }
    }

    private static RpcException PreparationQuota() => new(new Status(StatusCode.ResourceExhausted, "preparation exceeds request quota"));
}
