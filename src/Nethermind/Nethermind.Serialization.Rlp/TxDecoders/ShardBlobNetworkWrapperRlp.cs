// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.TxDecoders;

/// <summary>
/// RLP codec for the EIP-4844 / EIP-7594 blob-sidecar network wrapper
/// <c>[wrapper_version?, blobs, commitments, proofs]</c> shared by every blob-carrying transaction
/// type (EIP-4844 type-3 and EIP-8141 type-6).
/// </summary>
/// <remarks>
/// The <c>wrapper_version</c> scalar is present only for <see cref="ProofVersion.V1"/> (EIP-7594 cell
/// proofs); its absence denotes the legacy <see cref="ProofVersion.V0"/> form.
/// </remarks>
internal static class ShardBlobNetworkWrapperRlp
{
    private const int BlobCountLimit = 128;
    private const int BlobCellProofsCountLimit = BlobCountLimit * Ckzg.CellsPerExtBlob;

    private static readonly RlpLimit BlobsCountLimit = RlpLimit.For<ShardBlobNetworkWrapper>(BlobCountLimit, nameof(ShardBlobNetworkWrapper.Blobs));
    private static readonly RlpLimit CommitmentsCountLimit = RlpLimit.For<ShardBlobNetworkWrapper>(BlobCountLimit, nameof(ShardBlobNetworkWrapper.Commitments));
    private static readonly RlpLimit ProofsCountLimit = RlpLimit.For<ShardBlobNetworkWrapper>(BlobCountLimit, $"{nameof(ShardBlobNetworkWrapper.Proofs)} {ProofVersion.V0}");
    private static readonly RlpLimit CellProofsCountLimit = RlpLimit.For<ShardBlobNetworkWrapper>(BlobCellProofsCountLimit, $"{nameof(ShardBlobNetworkWrapper.Proofs)} {ProofVersion.V1}");

    public static ShardBlobNetworkWrapper Decode(ref RlpReader decoderContext)
    {
        ProofVersion version = ProofVersion.V0;
        if (!decoderContext.IsSequenceNext())
        {
            version = (ProofVersion)decoderContext.ReadByte();
            if (version > ProofVersion.V1)
            {
                throw new RlpException($"Unknown version of {nameof(ShardBlobNetworkWrapper)}. Expected no more than {(int)ProofVersion.V1} and is {version}");
            }
        }

        byte[][] blobs = decoderContext.DecodeByteArrays(BlobsCountLimit);
        byte[][] commitments = decoderContext.DecodeByteArrays(CommitmentsCountLimit);
        RlpLimit proofsCountLimit = version is ProofVersion.V1 ? CellProofsCountLimit : ProofsCountLimit;
        byte[][] proofs = decoderContext.DecodeByteArrays(proofsCountLimit);

        return new ShardBlobNetworkWrapper(blobs, commitments, proofs, version);
    }

    public static void Encode<TWriter>(ref TWriter writer, ShardBlobNetworkWrapper wrapper)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (wrapper.Version > ProofVersion.V0)
        {
            writer.Encode((byte)wrapper.Version);
        }

        writer.Encode(wrapper.Blobs);
        writer.Encode(wrapper.Commitments);
        writer.Encode(wrapper.Proofs);
    }

    public static int GetFieldsLength(ShardBlobNetworkWrapper wrapper) =>
        wrapper.Version switch { ProofVersion.V0 => 0, ProofVersion.V1 => 1, _ => throw new RlpException($"Unknown version of {nameof(ShardBlobNetworkWrapper)}: {wrapper.Version}") }
        + Rlp.LengthOf(wrapper.Blobs)
        + Rlp.LengthOf(wrapper.Commitments)
        + Rlp.LengthOf(wrapper.Proofs);
}
