// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <remarks>
/// <see cref="IEngineRpcModule"/> is taken via <see cref="Lazy{T}"/> to break the construction cycle
/// (the module composes this handler).
/// </remarks>
public sealed class NewPayloadWithWitnessHandler(
    Lazy<IEngineRpcModule> engineModule,
    WitnessRendezvous rendezvous,
    IBlockFinder blockFinder,
    BlockWitnessGenerator witnessGenerator,
    ILogManager? logManager = null) :
    IAsyncHandler<ExecutionPayloadParams<ExecutionPayloadV3>, NewPayloadWithWitnessV1Result>,
    IAsyncHandler<ExecutionPayloadParams<ExecutionPayloadV4>, NewPayloadWithWitnessV1Result>,
    IAsyncHandler<InclusionListExecutionPayloadParams, NewPayloadWithWitnessV1Result>
{
    /// <summary>Consensus clients resend payloads they already sent (e.g. after a restart), so recent witnesses are kept to answer those without re-executing.</summary>
    /// <remarks>Each cached witness keeps its node and code bytes alive (tens of MB for a large mainnet block), and a resend is
    /// almost always for the head or one of the last few blocks, so the count stays small.</remarks>
    private const int RecentWitnessCount = 4;

    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<NewPayloadWithWitnessHandler>();
    private readonly LruCache<Hash256, Witness> _recentWitnesses = new(RecentWitnessCount, nameof(NewPayloadWithWitnessHandler));

    public Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync(ExecutionPayloadParams<ExecutionPayloadV3> request) =>
        HandleAsync(
            request,
            nameof(IEngineRpcModule.engine_newPayloadWithWitnessV4),
            static (module, payload) => module.engine_newPayloadV4(
                payload.ExecutionPayload,
                payload.BlobVersionedHashes!,
                payload.ParentBeaconBlockRoot,
                payload.ExecutionRequests));

    public Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync(ExecutionPayloadParams<ExecutionPayloadV4> request) =>
        HandleAsync(
            request,
            nameof(IEngineRpcModule.engine_newPayloadWithWitnessV5),
            static (module, payload) => module.engine_newPayloadV5(
                payload.ExecutionPayload,
                payload.BlobVersionedHashes!,
                payload.ParentBeaconBlockRoot,
                payload.ExecutionRequests));

    /// <remarks>Inclusion is not part of the witness contract, so the V5 response shape is kept. The inclusion check is
    /// still scheduled, and its identifier is a function of the request alone, so it can be polled through
    /// <c>engine_getInclusionListVerdictV1</c>.</remarks>
    public Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync(InclusionListExecutionPayloadParams request) =>
        HandleAsync(
            request,
            nameof(IEngineRpcModule.engine_newPayloadWithWitnessV6),
            static async (module, payload) =>
            {
                ResultWrapper<PayloadStatusV2> result = await module.engine_newPayloadV6(
                    payload.ExecutionPayload,
                    payload.BlobVersionedHashes!,
                    payload.ParentBeaconBlockRoot,
                    payload.ExecutionRequests,
                    payload.InclusionLists,
                    payload.OmissionClaims);
                return ResultWrapper<PayloadStatusV1>.From(result);
            });

    private async Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync<TExecutionPayload>(
        ExecutionPayloadParams<TExecutionPayload> request,
        string methodName,
        Func<IEngineRpcModule, ExecutionPayloadParams<TExecutionPayload>, Task<ResultWrapper<PayloadStatusV1>>> submitPayload)
        where TExecutionPayload : ExecutionPayload
    {
        TExecutionPayload executionPayload = request.ExecutionPayload;
        Hash256? blockHash = executionPayload.BlockHash;

        if (blockHash is null)
        {
            if (_logger.IsWarn) _logger.Warn($"{methodName}: payload BlockHash is null — rejecting as InvalidParams.");
            return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                "executionPayload.blockHash is required", ErrorCodes.InvalidParams);
        }

        using WitnessRequest witnessRequest = rendezvous.RequestWitness(blockHash);

        using ResultWrapper<PayloadStatusV1> statusResult = await submitPayload(engineModule.Value, request);

        Witness? capturedWitness = witnessRequest.Task.IsCompletedSuccessfully ? witnessRequest.Task.Result : null;

        if (statusResult.Result.ResultType != ResultType.Success)
        {
            capturedWitness?.Dispose();
            return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                statusResult.Result.Error ?? $"{methodName}: payload processing failed",
                statusResult.ErrorCode);
        }

        PayloadStatusV1 payloadStatus = statusResult.Data!;
        Witness? witness = null;

        if (payloadStatus.Status == PayloadStatus.Valid)
        {
            // newPayload skips execution for a block it already knows, so nothing is captured, but a VALID
            // response must still carry the witness.
            witness = GetOrGenerateWitness(blockHash, capturedWitness, methodName);
            if (witness is null)
            {
                return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                    $"{methodName}: cannot generate a witness for VALID block {blockHash}: the block, its parent or the parent state is missing",
                    ErrorCodes.InternalError);
            }
        }
        else
            capturedWitness?.Dispose();

        return ResultWrapper<NewPayloadWithWitnessV1Result>.Success(
            NewPayloadWithWitnessV1Result.FromPayloadStatus(payloadStatus, witness));
    }

    /// <summary>Returns the witness of a VALID block: the captured one, a recently returned one, or one rebuilt by re-execution.</summary>
    /// <returns><c>null</c> when the witness must be rebuilt but the block, its parent or the parent state is missing.</returns>
    private Witness? GetOrGenerateWitness(Hash256 blockHash, Witness? capturedWitness, string methodName)
    {
        if (capturedWitness is null && _recentWitnesses.TryGet(blockHash, out Witness? cached))
            return cached;

        Witness? witness = capturedWitness ?? GenerateWitness(blockHash, methodName);
        if (witness is not null) _recentWitnesses.Set(blockHash, ToUnowned(witness));
        return witness;
    }

    /// <summary>Rebuilds the witness of an already-processed block by re-executing it on its parent state.</summary>
    /// <returns><c>null</c> when the block or its parent is not in the block tree, or the parent state is not available.</returns>
    /// <remarks>newPayload answers VALID for a known block below finalized even after its state is pruned.</remarks>
    private Witness? GenerateWitness(Hash256 blockHash, string methodName)
    {
        Block? block = blockFinder.FindBlock(blockHash);
        if (block is null) return null;

        if (_logger.IsDebug) _logger.Debug($"{methodName}: no witness captured for {blockHash}; re-executing the block to generate it.");
        return witnessGenerator.Generate(block);
    }

    /// <summary>Copies <paramref name="witness"/> into a witness that owns no pooled buffers.</summary>
    /// <remarks>Responses dispose their witness only after it is serialized, so a cached one must survive both
    /// that and eviction while another response may still be reading it.</remarks>
    internal static Witness ToUnowned(Witness witness) => new()
    {
        Codes = new UnownedList(witness.Codes.AsSpan().ToArray()),
        State = new UnownedList(witness.State.AsSpan().ToArray()),
        Keys = new UnownedList(witness.Keys.AsSpan().ToArray()),
        Headers = new UnownedList(witness.Headers.AsSpan().ToArray())
    };

    private sealed class UnownedList(byte[][] items) : IOwnedReadOnlyList<byte[]>
    {
        public int Count => items.Length;
        public byte[] this[int index] => items[index];
        public ReadOnlySpan<byte[]> AsSpan() => items;
        public IEnumerator<byte[]> GetEnumerator() => ((IEnumerable<byte[]>)items).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();
        public void Dispose() { }
    }
}
