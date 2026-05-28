// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz.SszVectorConverters;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum.Ssz.Test;

[TestFixture]
public class SszBasicTypeTests
{
    [TestCaseSource(nameof(BooleanValidCases))]
    public void Boolean_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        bool expected = yamlValue == "true";
        bool decoded = DecodeBool(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        Span<byte> reencoded = stackalloc byte[1];
        BooleanSszVectorConverter.ToSpan(reencoded, decoded);
        Assert.That(reencoded.ToArray(), Is.EqualTo(ssz));

        UInt256 root = MerkleizeWithConverter(decoded, BooleanSszVectorConverter.Feed);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(BooleanInvalidCases))]
    public void Boolean_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));

        Assert.That(() => DecodeBool(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    [TestCaseSource(nameof(Uint8ValidCases))]
    public void Uint8_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        byte expected = byte.Parse(yamlValue);
        byte decoded = DecodeByte(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        Span<byte> reencoded = stackalloc byte[1];
        ByteSszVectorConverter.ToSpan(reencoded, decoded);
        Assert.That(reencoded.ToArray(), Is.EqualTo(ssz));

        UInt256 root = MerkleizeWithConverter(decoded, ByteSszVectorConverter.Feed);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(Uint8InvalidCases))]
    public void Uint8_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => DecodeByte(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    [TestCaseSource(nameof(Uint16ValidCases))]
    public void Uint16_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        ushort expected = ushort.Parse(yamlValue);
        ushort decoded = DecodeUShort(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        Span<byte> reencoded = stackalloc byte[2];
        UInt16SszVectorConverter.ToSpan(reencoded, decoded);
        Assert.That(reencoded.ToArray(), Is.EqualTo(ssz));

        UInt256 root = MerkleizeWithConverter(decoded, UInt16SszVectorConverter.Feed);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(Uint16InvalidCases))]
    public void Uint16_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => DecodeUShort(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    [TestCaseSource(nameof(Uint32ValidCases))]
    public void Uint32_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        uint expected = uint.Parse(yamlValue);
        uint decoded = DecodeUInt(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        Span<byte> reencoded = stackalloc byte[4];
        UInt32SszVectorConverter.ToSpan(reencoded, decoded);
        Assert.That(reencoded.ToArray(), Is.EqualTo(ssz));

        UInt256 root = MerkleizeWithConverter(decoded, UInt32SszVectorConverter.Feed);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(Uint32InvalidCases))]
    public void Uint32_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => DecodeUInt(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    [TestCaseSource(nameof(Uint64ValidCases))]
    public void Uint64_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        ulong expected = ulong.Parse(yamlValue);
        ulong decoded = DecodeULong(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        Span<byte> reencoded = stackalloc byte[8];
        UInt64SszVectorConverter.ToSpan(reencoded, decoded);
        Assert.That(reencoded.ToArray(), Is.EqualTo(ssz));

        UInt256 root = MerkleizeWithConverter(decoded, UInt64SszVectorConverter.Feed);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(Uint64InvalidCases))]
    public void Uint64_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => DecodeULong(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    [TestCaseSource(nameof(Uint128ValidCases))]
    public void Uint128_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        UInt128 expected = UInt128.Parse(yamlValue);
        UInt128 decoded = DecodeUInt128(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        Span<byte> reencoded = stackalloc byte[16];
        UInt128SszVectorConverter.ToSpan(reencoded, decoded);
        Assert.That(reencoded.ToArray(), Is.EqualTo(ssz));

        UInt256 root = MerkleizeWithConverter(decoded, UInt128SszVectorConverter.Feed);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(Uint128InvalidCases))]
    public void Uint128_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => DecodeUInt128(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    [TestCaseSource(nameof(Uint256ValidCases))]
    public void Uint256_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        UInt256 expected = UInt256.Parse(yamlValue);
        UInt256 decoded = DecodeUInt256(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        byte[] reencoded = new byte[32];
        UInt256SszVectorConverter.ToSpan(reencoded.AsSpan(), decoded);
        Assert.That(reencoded, Is.EqualTo(ssz));

        UInt256 root = MerkleizeWithConverter(decoded, UInt256SszVectorConverter.Feed);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(Uint256InvalidCases))]
    public void Uint256_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => DecodeUInt256(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    private static UInt256 DecodeUInt256(ReadOnlySpan<byte> span)
    {
        if (span.Length != UInt256SszVectorConverter.Length)
        {
            throw new InvalidDataException(
                $"uint256 expects input of length {UInt256SszVectorConverter.Length} and received {span.Length}");
        }

        return UInt256SszVectorConverter.FromSpan(span);
    }

    private static bool DecodeBool(ReadOnlySpan<byte> span)
    {
        ValidateLength(span, BooleanSszVectorConverter.Length);
        return BooleanSszVectorConverter.FromSpan(span);
    }

    private static byte DecodeByte(ReadOnlySpan<byte> span)
    {
        ValidateLength(span, ByteSszVectorConverter.Length);
        return ByteSszVectorConverter.FromSpan(span);
    }

    private static ushort DecodeUShort(ReadOnlySpan<byte> span)
    {
        ValidateLength(span, UInt16SszVectorConverter.Length);
        return UInt16SszVectorConverter.FromSpan(span);
    }

    private static uint DecodeUInt(ReadOnlySpan<byte> span)
    {
        ValidateLength(span, UInt32SszVectorConverter.Length);
        return UInt32SszVectorConverter.FromSpan(span);
    }

    private static ulong DecodeULong(ReadOnlySpan<byte> span)
    {
        ValidateLength(span, UInt64SszVectorConverter.Length);
        return UInt64SszVectorConverter.FromSpan(span);
    }

    private static UInt128 DecodeUInt128(ReadOnlySpan<byte> span)
    {
        ValidateLength(span, UInt128SszVectorConverter.Length);
        return UInt128SszVectorConverter.FromSpan(span);
    }

    private static void ValidateLength(ReadOnlySpan<byte> span, int expectedLength)
    {
        if (span.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"SSZ decode expects input of length {expectedLength} and received {span.Length}");
        }
    }

    private delegate void FeedItem<T>(ref Merkleizer merkleizer, T value);

    private static UInt256 MerkleizeWithConverter<T>(T value, FeedItem<T> feed)
    {
        Merkleizer merkleizer = new(0);
        feed(ref merkleizer, value);
        merkleizer.CalculateRoot(out UInt256 root);
        return root;
    }

    private static string ReadYamlValue(string filePath)
    {
        using StreamReader reader = new(filePath);
        YamlStream yaml = [];
        yaml.Load(reader);
        YamlScalarNode rootNode = (YamlScalarNode)yaml.Documents[0].RootNode;
        return rootNode.Value!;
    }

    private static IEnumerable<TestCaseData> BooleanValidCases() => GetCases("boolean", "valid");
    private static IEnumerable<TestCaseData> BooleanInvalidCases() => GetCases("boolean", "invalid");
    private static IEnumerable<TestCaseData> Uint8ValidCases() => GetCases("uints", "valid", "uint_8_");
    private static IEnumerable<TestCaseData> Uint8InvalidCases() => GetCases("uints", "invalid", "uint_8_");
    private static IEnumerable<TestCaseData> Uint16ValidCases() => GetCases("uints", "valid", "uint_16_");
    private static IEnumerable<TestCaseData> Uint16InvalidCases() => GetCases("uints", "invalid", "uint_16_");
    private static IEnumerable<TestCaseData> Uint32ValidCases() => GetCases("uints", "valid", "uint_32_");
    private static IEnumerable<TestCaseData> Uint32InvalidCases() => GetCases("uints", "invalid", "uint_32_");
    private static IEnumerable<TestCaseData> Uint64ValidCases() => GetCases("uints", "valid", "uint_64_");
    private static IEnumerable<TestCaseData> Uint64InvalidCases() => GetCases("uints", "invalid", "uint_64_");
    private static IEnumerable<TestCaseData> Uint128ValidCases() => GetCases("uints", "valid", "uint_128_");
    private static IEnumerable<TestCaseData> Uint128InvalidCases() => GetCases("uints", "invalid", "uint_128_");
    private static IEnumerable<TestCaseData> Uint256ValidCases() => GetCases("uints", "valid", "uint_256_");
    private static IEnumerable<TestCaseData> Uint256InvalidCases() => GetCases("uints", "invalid", "uint_256_");

    private static IEnumerable<TestCaseData> GetCases(string handler, string validity, string? casePrefix = null)
    {
        string handlerPath = SszConsensusTestLoader.GetHandlerPath(handler);
        string validityPath = Path.Combine(handlerPath, validity);
        if (!Directory.Exists(validityPath))
            yield break;

        foreach (string casePath in Directory.GetDirectories(validityPath))
        {
            string caseName = Path.GetFileName(casePath);
            if (casePrefix is not null && !caseName.StartsWith(casePrefix, StringComparison.Ordinal))
                continue;

            yield return new TestCaseData(casePath).SetName($"{handler}/{validity}/{caseName}");
        }
    }
}
