// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum.Ssz.Test;

[TestFixture]
public class SszBasicTypeTests
{
    [TestCaseSource(nameof(BooleanValidCases))]
    public void Boolean_valid(string casePath) =>
        AssertBasicValid<BasicBoolTypeContainer, bool>(casePath, static value => value == "true", static container => container.Value);

    [TestCaseSource(nameof(BooleanInvalidCases))]
    public void Boolean_invalid(string casePath) =>
        AssertBasicInvalid<BasicBoolTypeContainer>(casePath);

    [TestCaseSource(nameof(Uint8ValidCases))]
    public void Uint8_valid(string casePath) =>
        AssertBasicValid<BasicByteTypeContainer, byte>(casePath, byte.Parse, static container => container.Value);

    [TestCaseSource(nameof(Uint8InvalidCases))]
    public void Uint8_invalid(string casePath) =>
        AssertBasicInvalid<BasicByteTypeContainer>(casePath);

    [TestCaseSource(nameof(Uint16ValidCases))]
    public void Uint16_valid(string casePath) =>
        AssertBasicValid<BasicUInt16TypeContainer, ushort>(casePath, ushort.Parse, static container => container.Value);

    [TestCaseSource(nameof(Uint16InvalidCases))]
    public void Uint16_invalid(string casePath) =>
        AssertBasicInvalid<BasicUInt16TypeContainer>(casePath);

    [TestCaseSource(nameof(Uint32ValidCases))]
    public void Uint32_valid(string casePath) =>
        AssertBasicValid<BasicUInt32TypeContainer, uint>(casePath, uint.Parse, static container => container.Value);

    [TestCaseSource(nameof(Uint32InvalidCases))]
    public void Uint32_invalid(string casePath) =>
        AssertBasicInvalid<BasicUInt32TypeContainer>(casePath);

    [TestCaseSource(nameof(Uint64ValidCases))]
    public void Uint64_valid(string casePath) =>
        AssertBasicValid<BasicUInt64TypeContainer, ulong>(casePath, ulong.Parse, static container => container.Value);

    [TestCaseSource(nameof(Uint64InvalidCases))]
    public void Uint64_invalid(string casePath) =>
        AssertBasicInvalid<BasicUInt64TypeContainer>(casePath);

    [TestCaseSource(nameof(Uint128ValidCases))]
    public void Uint128_valid(string casePath) =>
        AssertBasicValid<BasicUInt128TypeContainer, UInt128>(casePath, UInt128.Parse, static container => container.Value);

    [TestCaseSource(nameof(Uint128InvalidCases))]
    public void Uint128_invalid(string casePath) =>
        AssertBasicInvalid<BasicUInt128TypeContainer>(casePath);

    [TestCaseSource(nameof(Uint256ValidCases))]
    public void Uint256_valid(string casePath) =>
        AssertBasicValid<BasicUInt256TypeContainer, UInt256>(casePath, UInt256.Parse, static container => container.Value);

    [TestCaseSource(nameof(Uint256InvalidCases))]
    public void Uint256_invalid(string casePath) =>
        AssertBasicInvalid<BasicUInt256TypeContainer>(casePath);

    private static void AssertBasicValid<TContainer, TValue>(
        string casePath,
        Func<string, TValue> parseExpected,
        Func<TContainer, TValue> getValue)
        where TContainer : ISszCodec<TContainer>
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        TValue expected = parseExpected(ReadYamlValue(Path.Combine(casePath, "value.yaml")));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        TContainer.Decode(ssz, out TContainer decoded);
        byte[] reencoded = TContainer.Encode(decoded);
        TContainer.Merkleize(decoded, out UInt256 root);

        Assert.That(getValue(decoded), Is.EqualTo(expected));
        Assert.That(reencoded, Is.EqualTo(ssz));
        Assert.That(root, Is.EqualTo(expectedRoot));
    }

    private static void AssertBasicInvalid<TContainer>(string casePath)
        where TContainer : ISszCodec<TContainer>
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));

        Assert.That(() => TContainer.Decode(ssz, out TContainer _), Throws.InstanceOf<Exception>());
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
