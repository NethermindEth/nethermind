// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.FluentRlp;

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

    /// <param name="reader">A reader over the items of the enclosing wrapper sequence, after the transaction payload.
    /// The optional storage-only trailer is detected within it.</param>
    public static ShardBlobNetworkWrapper Decode<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TCanonicalChecks : struct, IFlag
    {
        ProofVersion version = ProofVersion.V0;
        if (!reader.IsNextSequence && !reader.IsNextItemEmptyByteArray())
        {
            // The version was read as a raw byte, so the non-canonical 0x00 for V0 stays accepted.
            version = reader.PeekByte() == 0
                ? (ProofVersion)reader.ReadRaw(1)[0]
                : (ProofVersion)reader.DecodeByte<TCanonicalChecks>();
            if (version > ProofVersion.V1)
            {
                throw new RlpReaderException($"Unknown version of {nameof(ShardBlobNetworkWrapper)}. Expected no more than {(int)ProofVersion.V1} and is {version}");
            }
        }

        byte[][] blobs;
        if (reader.TryReadEmptyBytes())
        {
            blobs = [];
        }
        else
        {
            blobs = (rlpBehaviors & RlpBehaviors.PoolBlobBuffers) != 0
                ? DecodePooledBlobs<TCanonicalChecks>(ref reader)
                : reader.DecodeByteArrays<TCanonicalChecks>(BlobsCountLimit);
        }

        PooledBlobBuffers? pooledBuffers = blobs.Length != 0 && (rlpBehaviors & RlpBehaviors.PoolBlobBuffers) != 0
            ? new(blobs) : null;
        try
        {
            byte[][] commitments = reader.DecodeByteArrays<TCanonicalChecks>(CommitmentsCountLimit);
            RlpLimit proofsCountLimit = version is ProofVersion.V1 ? CellProofsCountLimit : ProofsCountLimit;
            byte[][] proofs = reader.DecodeByteArrays<TCanonicalChecks>(proofsCountLimit);
            BlobCellMask cellMask = default;
            byte[][]? cells = null;

            if ((rlpBehaviors & RlpBehaviors.Storage) != 0 && HasTwoItems<TCanonicalChecks>(in reader))
            {
                cellMask = BlobCellMask.FromBytes(reader.DecodeByteArraySpan<TCanonicalChecks>());
                byte[][] decodedCells = reader.DecodeByteArrays<TCanonicalChecks>(CellProofsCountLimit);
                cells = cellMask.IsEmpty && decodedCells.Length == 0 ? null : decodedCells;
            }

            return new ShardBlobNetworkWrapper(blobs, commitments, proofs, version, cellMask, cells)
            {
                PooledBuffers = pooledBuffers
            };
        }
        catch
        {
            pooledBuffers?.Return();
            throw;
        }
    }

    private static bool HasTwoItems<TCanonicalChecks>(scoped in RlpReader reader) where TCanonicalChecks : struct, IFlag
    {
        if (!reader.HasNext) return false;
        RlpReader copy = reader;
        copy.SkipItem<TCanonicalChecks>();
        return copy.HasNext;
    }

    private static byte[][] DecodePooledBlobs<TCanonicalChecks>(ref RlpReader reader) where TCanonicalChecks : struct, IFlag
    {
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        int count = items.CountItems<TCanonicalChecks>(BlobsCountLimit);
        if (count == 0)
        {
            reader.ExitSequence(in items, itemLength);
            return [];
        }

        byte[][] blobs = new byte[count][];
        try
        {
            for (int i = 0; i < count; i++)
                blobs[i] = PooledBlobBuffers.Copy(items.DecodeByteArraySpan<TCanonicalChecks>());
            reader.ExitSequence(in items, itemLength);
            return blobs;
        }
        catch
        {
            new PooledBlobBuffers(blobs).Return();
            throw;
        }
    }

    [SkipLocalsInit]
    public static void Encode(ref RlpWriter writer, ShardBlobNetworkWrapper wrapper, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
        wrapper.Version switch { ProofVersion.V0 => 0, ProofVersion.V1 => 1, _ => throw new RlpReaderException($"Unknown version of {nameof(ShardBlobNetworkWrapper)}: {wrapper.Version}") }
        + Rlp.LengthOf(wrapper.Blobs)
        + Rlp.LengthOf(wrapper.Commitments)
        + Rlp.LengthOf(wrapper.Proofs)
        + (rlpBehaviors.HasFlag(RlpBehaviors.Storage)
            ? Rlp.LengthOfByteString(BlobCellMask.FixedByteLength, firstByte: 0) + Rlp.LengthOf(wrapper.Cells ?? [])
            : 0);
}
