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
}
