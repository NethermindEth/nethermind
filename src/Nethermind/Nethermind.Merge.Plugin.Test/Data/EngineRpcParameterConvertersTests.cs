// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Text;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Data;

[TestFixture]
public class EngineRpcParameterConvertersTests
{
    [Test]
    public void Blob_hash_converter_accepts_protocol_maximum()
    {
        string json = BuildHashArray(GetBlobsV4Limits.MaxBlobVersionedHashes, Hash256.Size);
        JsonSerializerOptions options = CreateOptions(new BlobVersionedHashesV4Converter());

        byte[][]? hashes = JsonSerializer.Deserialize<byte[][]>(json, options);

        Assert.That(hashes, Has.Length.EqualTo(GetBlobsV4Limits.MaxBlobVersionedHashes));
    }

    [Test]
    public void Blob_hash_converter_rejects_count_above_protocol_maximum()
    {
        string json = BuildHashArray(GetBlobsV4Limits.MaxBlobVersionedHashes + 1, Hash256.Size);
        JsonSerializerOptions options = CreateOptions(new BlobVersionedHashesV4Converter());

        Assert.That(
            () => JsonSerializer.Deserialize<byte[][]>(json, options),
            Throws.TypeOf<JsonException>());
    }

    [TestCase(Hash256.Size - 1)]
    [TestCase(Hash256.Size + 1)]
    [TestCase(1_024)]
    public void Blob_hash_converter_rejects_non_hash_width_before_conversion(int byteLength)
    {
        string json = BuildHashArray(1, byteLength);
        JsonSerializerOptions options = CreateOptions(new BlobVersionedHashesV4Converter());

        Assert.That(
            () => JsonSerializer.Deserialize<byte[][]>(json, options),
            Throws.TypeOf<JsonException>());
    }

    [TestCase(BlobCellMask.FixedByteLength - 1)]
    [TestCase(BlobCellMask.FixedByteLength + 1)]
    [TestCase(1_024)]
    public void Blob_cell_mask_converter_rejects_non_protocol_width(int byteLength)
    {
        string json = BuildHexString(byteLength);
        JsonSerializerOptions options = CreateOptions(new BlobCellBitArrayConverter());

        Assert.That(
            () => JsonSerializer.Deserialize<BitArray>(json, options),
            Throws.TypeOf<JsonException>());
    }

    [Test]
    public void Blob_cell_mask_converter_accepts_exact_protocol_width()
    {
        JsonSerializerOptions options = CreateOptions(new BlobCellBitArrayConverter());

        BitArray? mask = JsonSerializer.Deserialize<BitArray>(BuildHexString(BlobCellMask.FixedByteLength), options);

        Assert.That(mask, Has.Length.EqualTo(BlobCellMask.CellCount));
    }

    private static JsonSerializerOptions CreateOptions(System.Text.Json.Serialization.JsonConverter converter)
    {
        JsonSerializerOptions options = new();
        options.Converters.Add(converter);
        return options;
    }

    private static string BuildHashArray(int count, int byteLength)
    {
        string hash = BuildHexString(byteLength);
        StringBuilder builder = new(2 + (count * (hash.Length + 1)));
        builder.Append('[');
        for (int i = 0; i < count; i++)
        {
            if (i != 0)
            {
                builder.Append(',');
            }

            builder.Append(hash);
        }

        return builder.Append(']').ToString();
    }

    private static string BuildHexString(int byteLength) => $"\"0x{new string('0', byteLength * 2)}\"";
}
