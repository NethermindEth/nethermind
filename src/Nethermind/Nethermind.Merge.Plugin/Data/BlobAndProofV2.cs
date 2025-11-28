// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Ssz;
using System;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Nethermind.Merge.Plugin.Data;

[SszSerializable]
public class BlobAndProofV2
{
    public BlobAndProofV2(Blob blob, Proof[] proofs)
    {
        Blob = blob;
        Proofs = proofs;
    }

    public BlobAndProofV2()
    {
        Blob = default;
        Proofs = [];
    }

    [SszVector(Ckzg.BytesPerBlob)]
    public Blob Blob { get; set; }


    [SszVector(Ckzg.CellsPerExtBlob)]
    public Proof[] Proofs { get; set; }
}

[SszSerializable]
[InlineArray(32)]
[JsonConverter(typeof(InlineArrayJsonConverter<BlobVersionedHash>))]
public struct BlobVersionedHash : IInlineArrayConvertable<BlobVersionedHash>
{
    private byte _element0;

    public static implicit operator BlobVersionedHash(byte[] blobVersionedHash)
    {
        if (blobVersionedHash is not { Length: 32 })
        {
            throw new ArgumentException($"{nameof(BlobVersionedHash)} must be {32} bytes long");
        }

        BlobVersionedHash result = new();
        blobVersionedHash.CopyTo(result);

        return result;
    }

    public static implicit operator byte[](BlobVersionedHash blobVersionedHash)
    {
        return [.. blobVersionedHash];
    }

    public static byte[] ToBytes(BlobVersionedHash blobVersionedHash)
    {
        return (byte[])blobVersionedHash;
    }
    public static BlobVersionedHash ToType(byte[] bytes)
    {
        return (BlobVersionedHash)bytes;
    }
}

[SszSerializable]
[InlineArray(Ckzg.BytesPerProof)]
[JsonConverter(typeof(InlineArrayJsonConverter<Proof>))]
public struct Proof : IInlineArrayConvertable<Proof>
{
    private byte _element0;

    public static implicit operator Proof(byte[] proof)
    {
        if (proof is not { Length: Ckzg.BytesPerProof })
        {
            throw new ArgumentException($"{nameof(Proof)} must be {Ckzg.BytesPerProof} bytes long");
        }

        Proof result = new();
        proof.CopyTo(result);

        return result;
    }

    public static implicit operator byte[](Proof blobVersionedHash)
    {
        return [.. blobVersionedHash];
    }
    public static byte[] ToBytes(Proof blobVersionedHash)
    {
        return (byte[])blobVersionedHash;
    }
    public static Proof ToType(byte[] bytes)
    {
        return (Proof)bytes;
    }
}

[SszSerializable]
[InlineArray(Ckzg.BytesPerBlob)]
[JsonConverter(typeof(InlineArrayJsonConverter<Blob>))]
public struct Blob : IInlineArrayConvertable<Blob>
{
    private byte _element0;

    public static implicit operator Blob(byte[] blob)
    {
        if (blob is not { Length: Ckzg.BytesPerBlob })
        {
            throw new ArgumentException($"{nameof(Blob)} must be {Ckzg.BytesPerBlob} bytes long");
        }

        Blob result = new();
        blob.CopyTo(result);

        return result;
    }
    public static implicit operator byte[](Blob blobVersionedHash)
    {
        return [.. blobVersionedHash];
    }

    public static byte[] ToBytes(Blob blobVersionedHash)
    {
        return (byte[])blobVersionedHash;
    }
    public static Blob ToType(byte[] bytes)
    {
        return (Blob)bytes;
    }
}
