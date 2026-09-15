// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using CkzgLib;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.TxDecoders;

/// <summary>
/// RLP codec for the EIP-7594 blob sidecar <c>[wrapper_version?, blobs, commitments, proofs]</c>, shared
/// by type-3 and type-6. The version scalar is absent for the legacy <see cref="ProofVersion.V0"/> form.
/// </summary>
/// <remarks>
/// <see cref="RlpBehaviors.Storage"/> appends the sparse cell mask and cells, which the persistent blob
/// pool needs to restore a partially populated sidecar but which never travel on the wire.
/// </remarks>
internal static class ShardBlobNetworkWrapperRlp
{
    /// <summary>Decode-side cap on blobs per transaction.</summary>
    internal const int BlobCountLimit = 128;
    private const int BlobCellProofsCountLimit = BlobCountLimit * Ckzg.CellsPerExtBlob;

    private static readonly RlpLimit BlobsCountLimit = RlpLimit.For<ShardBlobNetworkWrapper>(BlobCountLimit, nameof(ShardBlobNetworkWrapper.Blobs));
    private static readonly RlpLimit CommitmentsCountLimit = RlpLimit.For<ShardBlobNetworkWrapper>(BlobCountLimit, nameof(ShardBlobNetworkWrapper.Commitments));
    private static readonly RlpLimit ProofsCountLimit = RlpLimit.For<ShardBlobNetworkWrapper>(BlobCountLimit, $"{nameof(ShardBlobNetworkWrapper.Proofs)} {ProofVersion.V0}");
    private static readonly RlpLimit CellProofsCountLimit = RlpLimit.For<ShardBlobNetworkWrapper>(BlobCellProofsCountLimit, $"{nameof(ShardBlobNetworkWrapper.Proofs)} {ProofVersion.V1}");

    public static ShardBlobNetworkWrapper Decode(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ProofVersion version = ProofVersion.V0;
        if (!decoderContext.IsSequenceNext() && !decoderContext.IsNextItemEmptyByteArray())
        {
            version = (ProofVersion)decoderContext.ReadByte();
            if (version > ProofVersion.V1)
            {
                throw new RlpException($"Unknown version of {nameof(ShardBlobNetworkWrapper)}. Expected no more than {(int)ProofVersion.V1} and is {version}");
            }
        }

        byte[][] blobs;
        if (decoderContext.IsNextItemEmptyByteArray())
        {
            decoderContext.DecodeByteArraySpan();
            blobs = [];
        }
        else
        {
            blobs = decoderContext.DecodeByteArrays(BlobsCountLimit);
        }

        byte[][] commitments = decoderContext.DecodeByteArrays(CommitmentsCountLimit);
        RlpLimit proofsCountLimit = version is ProofVersion.V1 ? CellProofsCountLimit : ProofsCountLimit;
        byte[][] proofs = decoderContext.DecodeByteArrays(proofsCountLimit);
        BlobCellMask cellMask = default;
        byte[][]? cells = null;

        if (rlpBehaviors.HasFlag(RlpBehaviors.Storage) && decoderContext.PeekNumberOfItemsRemaining(maxSearch: 2) > 0)
        {
            cellMask = BlobCellMask.FromBytes(decoderContext.DecodeByteArraySpan());
            byte[][] decodedCells = decoderContext.DecodeByteArrays(CellProofsCountLimit);
            cells = cellMask.IsEmpty && decodedCells.Length == 0 ? null : decodedCells;
        }

        return new ShardBlobNetworkWrapper(blobs, commitments, proofs, version, cellMask, cells);
    }

    public static void Encode<TWriter>(ref TWriter writer, ShardBlobNetworkWrapper wrapper, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (wrapper.Version > ProofVersion.V0)
        {
            writer.Encode((byte)wrapper.Version);
        }

        writer.Encode(wrapper.Blobs);
        writer.Encode(wrapper.Commitments);
        writer.Encode(wrapper.Proofs);

        if (rlpBehaviors.HasFlag(RlpBehaviors.Storage))
        {
            Span<byte> cellMaskBytes = stackalloc byte[BlobCellMask.FixedByteLength];
            wrapper.CellMask.WriteTo(cellMaskBytes);
            writer.Encode(cellMaskBytes);
            writer.Encode(wrapper.Cells ?? []);
        }
    }

    public static int GetFieldsLength(ShardBlobNetworkWrapper wrapper, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        wrapper.Version switch { ProofVersion.V0 => 0, ProofVersion.V1 => 1, _ => throw new RlpException($"Unknown version of {nameof(ShardBlobNetworkWrapper)}: {wrapper.Version}") }
        + Rlp.LengthOf(wrapper.Blobs)
        + Rlp.LengthOf(wrapper.Commitments)
        + Rlp.LengthOf(wrapper.Proofs)
        + (rlpBehaviors.HasFlag(RlpBehaviors.Storage)
            ? Rlp.LengthOfByteString(BlobCellMask.FixedByteLength, firstByte: 0) + Rlp.LengthOf(wrapper.Cells ?? [])
            : 0);
}
