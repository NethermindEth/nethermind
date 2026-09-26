// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>Chunked data column sidecar response plumbing shared by the <c>data_column_sidecars_by_range</c> and <c>by_root</c> protocols.</summary>
public abstract class DataColumnSidecarsProtocolBase(BeaconChainSpec spec) : ReqRespProtocolBase
{
    /// <summary>
    /// The spec's <c>compute_max_request_data_column_sidecars()</c>: <c>MAX_REQUEST_BLOCKS_DENEB * NUMBER_OF_COLUMNS</c>.
    /// </summary>
    public const ulong MaxRequestDataColumnSidecars = BlocksProtocolBase.MaxRequestBlocks * (ulong)Eip7594DasConstants.NumberOfColumns;

    /// <summary>Not a spec constant (see <see cref="BlocksProtocolBase.MaxBlocksResponseDuration"/>): bounds the total wall-clock time a streamed sidecars response may take.</summary>
    protected static readonly TimeSpan MaxSidecarsResponseDuration = TimeSpan.FromSeconds(60);

    protected BeaconChainSpec Spec { get; } = spec;

    /// <summary>The context bytes of a sidecar chunk: the fork digest of the sidecar's block's slot epoch.</summary>
    /// <remarks>Callers must ensure <paramref name="sidecar"/> has a non-null <c>SignedBlockHeader.Message</c> first (see <see cref="ReadSidecarChunksAsync"/>); this indexes into it unconditionally.</remarks>
    protected byte[] ContextBytesFor(DataColumnSidecar sidecar) =>
        ForkDigest.Compute(Spec, Spec.GetEpoch(sidecar.SignedBlockHeader!.Message!.Slot));

    /// <param name="overallTimeout">Overrides <see cref="MaxSidecarsResponseDuration"/>; test-only seam, production call sites omit it.</param>
    protected async Task<IReadOnlyList<DataColumnSidecar>> ReadSidecarChunksAsync(Stream stream, int maxSidecars, string protocolId, TimeSpan? overallTimeout = null)
    {
        List<DataColumnSidecar> sidecars = [];
        using BoundedTimeout timeout = StartBoundedTimeout(TtfbTimeout + RespTimeout, overallTimeout ?? MaxSidecarsResponseDuration);
        CancellationTokenSource cts = timeout.Cts;
        try
        {
            while (await ReqRespFraming.ReadResponseChunkAsync(stream, ReqRespFraming.ForkContextLength, ReqRespFraming.MaxPayloadSize, cts.Token) is { } chunk)
            {
                if (chunk.Result != ReqRespFraming.ResponseCode.Success)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.PeerError);
                    throw ErrorChunkToException(chunk);
                }

                if (sidecars.Count >= maxSidecars)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.LimitExceeded);
                    throw new Eth2ReqRespException($"Peer responded with more than the requested {maxSidecars} data column sidecars");
                }

                DataColumnSidecar sidecar;
                try
                {
                    DataColumnSidecar.Decode(chunk.Payload, out sidecar);
                }
                catch (Exception e) when (e is not Eth2ReqRespException and not OperationCanceledException)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException($"Malformed data column sidecar chunk: {e.Message}");
                }

                // VerifyStructure does not check SignedBlockHeader itself. SSZ decode always populates
                // every field, so this should be unreachable for a wire-decoded chunk; kept as a guard
                // against the fork-digest check right below null-referencing if that ever changes.
                if (sidecar.SignedBlockHeader?.Message is null)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException("Data column sidecar chunk missing its signed block header");
                }

                if (!DataColumnSidecarVerifier.VerifyStructure(sidecar))
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException("Data column sidecar chunk failed structural validation");
                }

                if (!DataColumnSidecarVerifier.VerifyBlobCount(sidecar, Spec))
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException("Data column sidecar chunk carries more commitments than its epoch permits");
                }

                // gloas/p2p-interface.md: a Gloas-epoch sidecar has the Gloas shape, so a Fulu-shaped one claiming a Gloas slot is invalid.
                if (Spec.GetEpoch(sidecar.SignedBlockHeader.Message.Slot) >= Spec.GloasForkEpoch)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException($"Fulu data column sidecar chunk claims Gloas slot {sidecar.SignedBlockHeader.Message.Slot}");
                }

                if (!chunk.ContextBytes.AsSpan().SequenceEqual(ContextBytesFor(sidecar)))
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException($"Data column sidecar chunk context bytes do not match the fork digest of slot {sidecar.SignedBlockHeader.Message.Slot}");
                }

                sidecars.Add(sidecar);
                cts.CancelAfter(RespTimeout);
            }
        }
        catch (OperationCanceledException)
        {
            RecordFailure(protocolId, ReqRespFailureReason.Timeout);
            throw;
        }

        return sidecars;
    }

    protected Task WriteSidecarChunkAsync(Stream stream, DataColumnSidecar sidecar, CancellationTokenSource cts)
    {
        cts.CancelAfter(RespTimeout);
        return ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, ContextBytesFor(sidecar), DataColumnSidecar.Encode(sidecar), cts.Token);
    }

    /// <summary>The context bytes of a Gloas sidecar chunk: the fork digest of the sidecar's slot epoch.</summary>
    /// <remarks>gloas/p2p-interface.md gives no context table for the Gloas sidecar, which has no block header, so the epoch comes from its own <c>slot</c>.</remarks>
    protected byte[] ContextBytesFor(DataColumnSidecarGloas sidecar) =>
        ForkDigest.Compute(Spec, Spec.GetEpoch(sidecar.Slot));

    /// <summary>Reads Gloas-shaped sidecar chunks until the stream ends, refusing any chunk that fails a check that needs no bid.</summary>
    /// <remarks>
    /// Each chunk is bounded by gloas/p2p-interface.md <c>compute_max_data_column_sidecar_size</c>, must claim a Gloas-epoch
    /// slot with matching context bytes, name a block root, and pass the bid-free part of <c>verify_data_column_sidecar</c>.
    /// The commitment match and KZG proofs need the block's bid, so the caller checks them.
    /// </remarks>
    /// <param name="overallTimeout">Overrides <see cref="MaxSidecarsResponseDuration"/>; test-only seam, production call sites omit it.</param>
    protected async Task<IReadOnlyList<DataColumnSidecarGloas>> ReadGloasSidecarChunksAsync(Stream stream, int maxSidecars, string protocolId, TimeSpan? overallTimeout = null)
    {
        int maxChunkSize = (int)Math.Min(DataColumnSidecarGloasSize.ComputeMax(Spec), (ulong)ReqRespFraming.MaxPayloadSize);
        List<DataColumnSidecarGloas> sidecars = [];
        using BoundedTimeout timeout = StartBoundedTimeout(TtfbTimeout + RespTimeout, overallTimeout ?? MaxSidecarsResponseDuration);
        CancellationTokenSource cts = timeout.Cts;
        try
        {
            while (await ReqRespFraming.ReadResponseChunkAsync(stream, ReqRespFraming.ForkContextLength, maxChunkSize, cts.Token) is { } chunk)
            {
                if (chunk.Result != ReqRespFraming.ResponseCode.Success)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.PeerError);
                    throw ErrorChunkToException(chunk);
                }

                if (sidecars.Count >= maxSidecars)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.LimitExceeded);
                    throw new Eth2ReqRespException($"Peer responded with more than the requested {maxSidecars} data column sidecars");
                }

                DataColumnSidecarGloas sidecar;
                try
                {
                    DataColumnSidecarGloas.Decode(chunk.Payload, out sidecar);
                }
                catch (Exception e) when (e is not Eth2ReqRespException and not OperationCanceledException)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException($"Malformed Gloas data column sidecar chunk: {e.Message}");
                }

                if (sidecar.BeaconBlockRoot is null || !HasGloasStructure(sidecar))
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException("Gloas data column sidecar chunk failed structural validation");
                }

                if (Spec.GetEpoch(sidecar.Slot) < Spec.GloasForkEpoch)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException($"Gloas data column sidecar chunk claims pre-Gloas slot {sidecar.Slot}");
                }

                if (!chunk.ContextBytes.AsSpan().SequenceEqual(ContextBytesFor(sidecar)))
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException($"Gloas data column sidecar chunk context bytes do not match the fork digest of slot {sidecar.Slot}");
                }

                sidecars.Add(sidecar);
                cts.CancelAfter(RespTimeout);
            }
        }
        catch (OperationCanceledException)
        {
            RecordFailure(protocolId, ReqRespFailureReason.Timeout);
            throw;
        }

        return sidecars;
    }

    protected Task WriteGloasSidecarChunkAsync(Stream stream, DataColumnSidecarGloas sidecar, CancellationTokenSource cts)
    {
        cts.CancelAfter(RespTimeout);
        return ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, ContextBytesFor(sidecar), DataColumnSidecarGloas.Encode(sidecar), cts.Token);
    }

    // The bid-free part of gloas verify_data_column_sidecar; the bid's commitments are bounded by max_blobs_per_block at the block's epoch.
    private bool HasGloasStructure(DataColumnSidecarGloas sidecar) =>
        sidecar.Index < (ulong)Eip7594DasConstants.NumberOfColumns
        && sidecar.Column is { Length: > 0 } column
        && sidecar.KzgProofs is { } proofs
        && proofs.Length == column.Length
        && (ulong)column.Length <= (Spec.GetBlobParameters(Spec.GetEpoch(sidecar.Slot))?.MaxBlobsPerBlock ?? Spec.MaxBlobsPerBlockElectra);
}
