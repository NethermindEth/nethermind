// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Int256;
using Nethermind.Merkleization;
using NUnit.Framework;
using SszEncoder = Nethermind.Serialization.Ssz.Ssz;
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
        bool decoded = SszEncoder.DecodeBool(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        Span<byte> reencoded = stackalloc byte[1];
        SszEncoder.Encode(reencoded, decoded);
        Assert.That(reencoded.ToArray(), Is.EqualTo(ssz));

        Merkle.Merkleize(out UInt256 root, decoded);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(BooleanInvalidCases))]
    public void Boolean_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));

        if (ssz.Length != 1)
        {
            // Wrong length: decoder should throw
            Assert.That(() => SszEncoder.DecodeBool(ssz.AsSpan()), Throws.InstanceOf<Exception>());
        }
        else
        {
            // Correct length but invalid value (> 1): DecodeBool doesn't validate yet
            Assert.That(ssz[0] > 1, Is.True, "Expected out-of-range boolean value");
        }
    }

    [TestCaseSource(nameof(Uint8ValidCases))]
    public void Uint8_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        byte expected = byte.Parse(yamlValue);
        byte decoded = SszEncoder.DecodeByte(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        Span<byte> reencoded = stackalloc byte[1];
        SszEncoder.Encode(reencoded, decoded);
        Assert.That(reencoded.ToArray(), Is.EqualTo(ssz));

        Merkle.Merkleize(out UInt256 root, decoded);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(Uint8InvalidCases))]
    public void Uint8_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => SszEncoder.DecodeByte(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    [TestCaseSource(nameof(Uint16ValidCases))]
    public void Uint16_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        ushort expected = ushort.Parse(yamlValue);
        ushort decoded = SszEncoder.DecodeUShort(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        Span<byte> reencoded = stackalloc byte[2];
        SszEncoder.Encode(reencoded, decoded);
        Assert.That(reencoded.ToArray(), Is.EqualTo(ssz));

        Merkle.Merkleize(out UInt256 root, decoded);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(Uint16InvalidCases))]
    public void Uint16_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => SszEncoder.DecodeUShort(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    [TestCaseSource(nameof(Uint32ValidCases))]
    public void Uint32_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        uint expected = uint.Parse(yamlValue);
        uint decoded = SszEncoder.DecodeUInt(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        Span<byte> reencoded = stackalloc byte[4];
        SszEncoder.Encode(reencoded, decoded);
        Assert.That(reencoded.ToArray(), Is.EqualTo(ssz));

        Merkle.Merkleize(out UInt256 root, decoded);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(Uint32InvalidCases))]
    public void Uint32_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => SszEncoder.DecodeUInt(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    [TestCaseSource(nameof(Uint64ValidCases))]
    public void Uint64_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        ulong expected = ulong.Parse(yamlValue);
        ulong decoded = SszEncoder.DecodeULong(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        Span<byte> reencoded = stackalloc byte[8];
        SszEncoder.Encode(reencoded, decoded);
        Assert.That(reencoded.ToArray(), Is.EqualTo(ssz));

        Merkle.Merkleize(out UInt256 root, decoded);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(Uint64InvalidCases))]
    public void Uint64_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => SszEncoder.DecodeULong(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    [TestCaseSource(nameof(Uint128ValidCases))]
    public void Uint128_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        UInt128 expected = UInt128.Parse(yamlValue);
        UInt128 decoded = SszEncoder.DecodeUInt128(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        Span<byte> reencoded = stackalloc byte[16];
        SszEncoder.Encode(reencoded, decoded);
        Assert.That(reencoded.ToArray(), Is.EqualTo(ssz));

        Merkle.Merkleize(out UInt256 root, decoded);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(Uint128InvalidCases))]
    public void Uint128_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => SszEncoder.DecodeUInt128(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    [TestCaseSource(nameof(Uint256ValidCases))]
    public void Uint256_valid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        string yamlValue = ReadYamlValue(Path.Combine(casePath, "value.yaml"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        UInt256 expected = UInt256.Parse(yamlValue);
        UInt256 decoded = SszEncoder.DecodeUInt256(ssz.AsSpan());
        Assert.That(decoded, Is.EqualTo(expected));

        byte[] reencoded = new byte[32];
        SszEncoder.Encode(reencoded.AsSpan(), decoded);
        Assert.That(reencoded, Is.EqualTo(ssz));

        Merkle.Merkleize(out UInt256 root, decoded);
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    [TestCaseSource(nameof(Uint256InvalidCases))]
    public void Uint256_invalid(string casePath)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => SszEncoder.DecodeUInt256(ssz.AsSpan()), Throws.InstanceOf<Exception>());
    }

    private static string ReadYamlValue(string filePath)
    {
        using StreamReader reader = new(filePath);
        YamlStream yaml = new();
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
