// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Int256;
using Nethermind.Serialization;
using NUnit.Framework;
namespace Ethereum.Ssz.Test;

[TestFixture]
public class SszContainerTests
{
    private delegate void DecodeSszValue<T>(ReadOnlySpan<byte> data, out T value);
    private delegate void MerkleizeSszValue<T>(T value, out UInt256 root);

    private static readonly IReadOnlyDictionary<string, Action<byte[], UInt256>> ValidHandlers =
        new Dictionary<string, Action<byte[], UInt256>>(StringComparer.Ordinal)
        {
            [nameof(SingleFieldTestStruct)] = CreateValidHandler<SingleFieldTestStruct>(
                static (ReadOnlySpan<byte> data, out SingleFieldTestStruct value) => SszEncoding.Decode(data, out value),
                static value => SszEncoding.Encode(value),
                static (SingleFieldTestStruct value, out UInt256 root) => SszEncoding.Merkleize(value, out root)),
            [nameof(SmallTestStruct)] = CreateValidHandler<SmallTestStruct>(
                static (ReadOnlySpan<byte> data, out SmallTestStruct value) => SszEncoding.Decode(data, out value),
                static value => SszEncoding.Encode(value),
                static (SmallTestStruct value, out UInt256 root) => SszEncoding.Merkleize(value, out root)),
            [nameof(FixedTestStruct)] = CreateValidHandler<FixedTestStruct>(
                static (ReadOnlySpan<byte> data, out FixedTestStruct value) => SszEncoding.Decode(data, out value),
                static value => SszEncoding.Encode(value),
                static (FixedTestStruct value, out UInt256 root) => SszEncoding.Merkleize(value, out root)),
            [nameof(VarTestStruct)] = CreateValidHandler<VarTestStruct>(
                static (ReadOnlySpan<byte> data, out VarTestStruct value) => SszEncoding.Decode(data, out value),
                static value => SszEncoding.Encode(value),
                static (VarTestStruct value, out UInt256 root) => SszEncoding.Merkleize(value, out root)),
            [nameof(ComplexTestStruct)] = CreateValidHandler<ComplexTestStruct>(
                static (ReadOnlySpan<byte> data, out ComplexTestStruct value) => SszEncoding.Decode(data, out value),
                static value => SszEncoding.Encode(value),
                static (ComplexTestStruct value, out UInt256 root) => SszEncoding.Merkleize(value, out root)),
            [nameof(BitsStruct)] = CreateValidHandler<BitsStruct>(
                static (ReadOnlySpan<byte> data, out BitsStruct value) => SszEncoding.Decode(data, out value),
                static value => SszEncoding.Encode(value),
                static (BitsStruct value, out UInt256 root) => SszEncoding.Merkleize(value, out root)),
            [nameof(ProgressiveTestStruct)] = CreateValidHandler<ProgressiveTestStruct>(
                static (ReadOnlySpan<byte> data, out ProgressiveTestStruct value) => SszEncoding.Decode(data, out value),
                static value => SszEncoding.Encode(value),
                static (ProgressiveTestStruct value, out UInt256 root) => SszEncoding.Merkleize(value, out root)),
            [nameof(ProgressiveBitsStruct)] = CreateValidHandler<ProgressiveBitsStruct>(
                static (ReadOnlySpan<byte> data, out ProgressiveBitsStruct value) => SszEncoding.Decode(data, out value),
                static value => SszEncoding.Encode(value),
                static (ProgressiveBitsStruct value, out UInt256 root) => SszEncoding.Merkleize(value, out root)),
        };

    private static readonly IReadOnlyDictionary<string, Action<byte[]>> InvalidHandlers =
        new Dictionary<string, Action<byte[]>>(StringComparer.Ordinal)
        {
            [nameof(SingleFieldTestStruct)] = CreateInvalidHandler<SingleFieldTestStruct>(
                static (ReadOnlySpan<byte> data, out SingleFieldTestStruct value) => SszEncoding.Decode(data, out value)),
            [nameof(SmallTestStruct)] = CreateInvalidHandler<SmallTestStruct>(
                static (ReadOnlySpan<byte> data, out SmallTestStruct value) => SszEncoding.Decode(data, out value)),
            [nameof(FixedTestStruct)] = CreateInvalidHandler<FixedTestStruct>(
                static (ReadOnlySpan<byte> data, out FixedTestStruct value) => SszEncoding.Decode(data, out value)),
            [nameof(VarTestStruct)] = CreateInvalidHandler<VarTestStruct>(
                static (ReadOnlySpan<byte> data, out VarTestStruct value) => SszEncoding.Decode(data, out value)),
            [nameof(ComplexTestStruct)] = CreateInvalidHandler<ComplexTestStruct>(
                static (ReadOnlySpan<byte> data, out ComplexTestStruct value) => SszEncoding.Decode(data, out value)),
            [nameof(BitsStruct)] = CreateInvalidHandler<BitsStruct>(
                static (ReadOnlySpan<byte> data, out BitsStruct value) => SszEncoding.Decode(data, out value)),
            [nameof(ProgressiveTestStruct)] = CreateInvalidHandler<ProgressiveTestStruct>(
                static (ReadOnlySpan<byte> data, out ProgressiveTestStruct value) => SszEncoding.Decode(data, out value)),
            [nameof(ProgressiveBitsStruct)] = CreateInvalidHandler<ProgressiveBitsStruct>(
                static (ReadOnlySpan<byte> data, out ProgressiveBitsStruct value) => SszEncoding.Decode(data, out value)),
        };

    [TestCaseSource(nameof(ValidContainerCases))]
    public void Container_valid_roundtrip_and_root(string casePath, string containerType)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        Assert.That(ValidHandlers.TryGetValue(containerType, out Action<byte[], UInt256>? handler), Is.True,
            $"Unrecognized container type: {containerType} - add test support for it in {nameof(SszContainerTests)}");
        handler!(ssz, expectedRoot);
    }

    [TestCaseSource(nameof(InvalidContainerCases))]
    public void Container_invalid_should_fail(string casePath, string containerType)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));

        Assert.That(InvalidHandlers.TryGetValue(containerType, out Action<byte[]>? handler), Is.True,
            $"Unrecognized container type: {containerType} - add test support for it in {nameof(SszContainerTests)}");
        handler!(ssz);
    }

    // --- Helpers ---

    private static Action<byte[], UInt256> CreateValidHandler<T>(
        DecodeSszValue<T> decode,
        Func<T, byte[]> encode,
        MerkleizeSszValue<T> merkleize)
    {
        return (ssz, expectedRoot) =>
        {
            decode(ssz, out T decoded);
            byte[] reEncoded = encode(decoded);
            Assert.That(reEncoded, Is.EqualTo(ssz), "Re-encoded SSZ does not match original");

            merkleize(decoded, out UInt256 root);
            Assert.That(root, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
        };
    }

    private static Action<byte[]> CreateInvalidHandler<T>(DecodeSszValue<T> decode) =>
        ssz => Assert.That(() => decode(ssz, out T _), Throws.InstanceOf<Exception>());


    /// <summary>
    /// Extracts the container type from a case name like "BitsStruct_lengthy_0".
    /// </summary>
    private static string ExtractContainerType(string caseName)
    {
        int separatorIndex = caseName.IndexOf('_');
        return separatorIndex >= 0 ? caseName[..separatorIndex] : caseName;
    }

    // --- Test case sources ---
    // Structure: containers/valid/{case_name}/ and containers/invalid/{case_name}/
    // Case names: "{ContainerType}_{descriptor}" e.g. "BitsStruct_lengthy_0"

    private static IEnumerable<TestCaseData> ValidContainerCases() => GetCases("containers", "valid");
    private static IEnumerable<TestCaseData> InvalidContainerCases() => GetCases("containers", "invalid");

    private static IEnumerable<TestCaseData> GetCases(string handler, string validity)
    {
        string handlerPath = SszConsensusTestLoader.GetHandlerPath(handler);
        string validityPath = Path.Combine(handlerPath, validity);
        if (!Directory.Exists(validityPath))
            yield break;

        foreach (string casePath in Directory.GetDirectories(validityPath))
        {
            string caseName = Path.GetFileName(casePath);
            string containerType = ExtractContainerType(caseName);

            yield return new TestCaseData(casePath, containerType)
                .SetName($"containers/{validity}/{caseName}");
        }
    }
}
