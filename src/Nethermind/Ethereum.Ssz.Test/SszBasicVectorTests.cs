// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Int256;
using Nethermind.Merkleization;
using NUnit.Framework;
using SszEncoder = global::Nethermind.Serialization.Ssz.Ssz;
namespace Ethereum.Ssz.Test;

[TestFixture]
public class SszBasicVectorTests
{
    private static readonly Dictionary<string, int> ElementSizes = new()
    {
        ["bool"] = 1,
        ["uint8"] = 1,
        ["uint16"] = 2,
        ["uint32"] = 4,
        ["uint64"] = 8,
        ["uint128"] = 16,
        ["uint256"] = 32
    };

    /// <summary>
    /// Parses "vec_uint64_4_random" into (elementType: "uint64", vectorLength: 4).
    /// Case name format: vec_{type}_{length}_{descriptor}
    /// </summary>
    // Longest first to avoid "uint1" matching "uint16"
    private static readonly string[] Types = ["uint128", "uint256", "uint16", "uint32", "uint64", "uint8", "bool"];

    private static (string elementType, int vectorLength) ParseCaseName(string caseName)
    {
        // Strip "vec_" prefix
        string rest = caseName.Substring(4);
        foreach (string type in Types)
        {
            string typePrefix = type + "_";
            if (rest.StartsWith(typePrefix, StringComparison.Ordinal))
            {
                string afterType = rest.Substring(typePrefix.Length);
                int underscoreIdx = afterType.IndexOf('_');
                string lengthPart = underscoreIdx >= 0 ? afterType.Substring(0, underscoreIdx) : afterType;
                int vectorLength = int.Parse(lengthPart);
                return (type, vectorLength);
            }
        }

        throw new ArgumentException($"Cannot parse vector case name: {caseName}");
    }

    [TestCaseSource(nameof(ValidCases))]
    public void BasicVector_valid_roundtrip_and_root(string casePath, string caseName)
    {
        (string elementType, int vectorLength) = ParseCaseName(caseName);
        int elementSize = ElementSizes[elementType];
        int expectedByteLength = vectorLength * elementSize;

        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(ssz.Length, Is.EqualTo(expectedByteLength),
            $"SSZ length {ssz.Length} does not match expected {expectedByteLength} for {caseName}");

        // Decode and re-encode to verify round-trip
        byte[] reEncoded = new byte[expectedByteLength];
        VerifyDecodeReencode(elementType, ssz, reEncoded);
        Assert.That(reEncoded, Is.EqualTo(ssz), $"Re-encoded SSZ does not match original for {caseName}");

        // Verify hash tree root
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));
        Merkle.Merkleize(out UInt256 computedRoot, (ReadOnlySpan<byte>)ssz);
        Assert.That(computedRoot, Is.EqualTo(expectedRoot), $"Hash tree root mismatch for {caseName}");
    }

    [TestCaseSource(nameof(InvalidCases))]
    public void BasicVector_invalid_should_fail(string casePath, string caseName)
    {
        (string elementType, int vectorLength) = ParseCaseName(caseName);
        int elementSize = ElementSizes[elementType];
        int expectedByteLength = vectorLength * elementSize;

        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));

        // Zero-length vectors are invalid by definition
        if (vectorLength == 0)
        {
            Assert.Pass("Zero-length vector is invalid by definition");
            return;
        }

        // Wrong byte length: decoders validate alignment and will throw
        if (ssz.Length != expectedByteLength)
        {
            byte[] reEncoded = new byte[ssz.Length];
            Assert.That(() => VerifyDecodeReencode(elementType, ssz, reEncoded), Throws.InstanceOf<Exception>(),
                $"Decoder should reject wrong-length input for {caseName}");
            return;
        }

        // Correct length but invalid values (e.g. bool > 1): DecodeBools doesn't validate yet
        if (elementType == "bool")
        {
            bool hasInvalidBool = false;
            for (int i = 0; i < ssz.Length; i++)
            {
                if (ssz[i] > 1)
                {
                    hasInvalidBool = true;
                    break;
                }
            }
            Assert.That(hasInvalidBool, Is.True, $"Expected out-of-range boolean value for {caseName}");
            return;
        }

        Assert.Fail($"Unhandled invalid basic_vector case: {caseName}");
    }

    private static void VerifyDecodeReencode(string elementType, byte[] ssz, byte[] reEncoded)
    {
        switch (elementType)
        {
            case "bool":
                Span<bool> decodedBools = SszEncoder.DecodeBools(ssz);
                SszEncoder.Encode(reEncoded.AsSpan(), decodedBools);
                break;
            case "uint8":
                SszEncoder.Encode(reEncoded.AsSpan(), (ReadOnlySpan<byte>)ssz);
                break;
            case "uint16":
                Span<ushort> decodedUshorts = SszEncoder.DecodeUShorts(ssz);
                SszEncoder.Encode(reEncoded.AsSpan(), decodedUshorts);
                break;
            case "uint32":
                Span<uint> decodedUints = SszEncoder.DecodeUInts(ssz);
                SszEncoder.Encode(reEncoded.AsSpan(), decodedUints);
                break;
            case "uint64":
                Span<ulong> decodedUlongs = SszEncoder.DecodeULongs(ssz);
                SszEncoder.Encode(reEncoded.AsSpan(), decodedUlongs);
                break;
            case "uint128":
                UInt128[] decodedUint128S = SszEncoder.DecodeUInts128(ssz);
                SszEncoder.Encode(reEncoded.AsSpan(), decodedUint128S);
                break;
            case "uint256":
                UInt256[] decodedUint256S = SszEncoder.DecodeUInts256(ssz);
                SszEncoder.Encode(reEncoded.AsSpan(), decodedUint256S);
                break;
            default:
                Assert.Fail($"Unsupported element type: {elementType}");
                break;
        }
    }


    // --- Test case sources ---
    // Structure: basic_vector/valid/{case_name}/ and basic_vector/invalid/{case_name}/
    // Case names: "vec_{type}_{length}_{descriptor}"

    private static IEnumerable<TestCaseData> ValidCases()
    {
        return GetCases("basic_vector", "valid");
    }

    private static IEnumerable<TestCaseData> InvalidCases()
    {
        return GetCases("basic_vector", "invalid");
    }

    private static IEnumerable<TestCaseData> GetCases(string handler, string validity)
    {
        string handlerPath = SszConsensusTestLoader.GetHandlerPath(handler);
        string validityPath = Path.Combine(handlerPath, validity);
        if (!Directory.Exists(validityPath))
            yield break;

        foreach (string casePath in Directory.GetDirectories(validityPath))
        {
            string caseName = Path.GetFileName(casePath);
            yield return new TestCaseData(casePath, caseName)
                .SetName($"basic_vector/{validity}/{caseName}");
        }
    }
}
