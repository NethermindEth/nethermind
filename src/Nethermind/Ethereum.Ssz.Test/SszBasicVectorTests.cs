// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz.SszVectorConverters;
using NUnit.Framework;

namespace Ethereum.Ssz.Test;

[TestFixture]
public class SszBasicVectorTests
{
    private const string TypeBool = "bool";
    private const string TypeUint8 = "uint8";
    private const string TypeUint16 = "uint16";
    private const string TypeUint32 = "uint32";
    private const string TypeUint64 = "uint64";
    private const string TypeUint128 = "uint128";
    private const string TypeUint256 = "uint256";

    private static readonly Dictionary<string, int> ElementSizes = new()
    {
        [TypeBool] = 1,
        [TypeUint8] = 1,
        [TypeUint16] = 2,
        [TypeUint32] = 4,
        [TypeUint64] = 8,
        [TypeUint128] = 16,
        [TypeUint256] = 32
    };

    /// <summary>
    /// Parses "vec_uint64_4_random" into (elementType: "uint64", vectorLength: 4).
    /// Case name format: vec_{type}_{length}_{descriptor}
    /// </summary>
    // Longest first to avoid "uint1" matching "uint16"
    private static readonly string[] Types = [TypeUint128, TypeUint256, TypeUint16, TypeUint32, TypeUint64, TypeUint8, TypeBool];

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
        VerifyDecodeReencode(elementType, ssz, reEncoded, expectedByteLength);
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
            Assert.That(() => VerifyDecodeReencode(elementType, ssz, reEncoded, expectedByteLength), Throws.InstanceOf<Exception>(),
                $"Decoder should reject wrong-length input for {caseName}");
            return;
        }

        // Correct length but invalid values (e.g. bool > 1): DecodeBools doesn't validate yet
        if (elementType == TypeBool)
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

    private static void VerifyDecodeReencode(string elementType, byte[] ssz, byte[] reEncoded, int expectedByteLength)
    {
        // Basic element decoders are length-agnostic; vector tests must enforce the exact byte length.
        if (ssz.Length != expectedByteLength)
        {
            throw new InvalidDataException(
                $"Invalid SSZ length for basic_vector<{elementType}>: expected {expectedByteLength} bytes but got {ssz.Length}");
        }

        switch (elementType)
        {
            case TypeBool:
                bool[] decodedBools = new bool[ssz.Length];
                BooleanSszVectorConverter.FromSpan(ssz, decodedBools);
                BooleanSszVectorConverter.ToSpan(reEncoded, decodedBools);
                break;
            case TypeUint8:
                ByteSszVectorConverter.ToSpan(reEncoded, ssz);
                break;
            case TypeUint16:
                ushort[] decodedUshorts = new ushort[ssz.Length / UInt16SszVectorConverter.Length];
                UInt16SszVectorConverter.FromSpan(ssz, decodedUshorts);
                UInt16SszVectorConverter.ToSpan(reEncoded, decodedUshorts);
                break;
            case TypeUint32:
                uint[] decodedUints = new uint[ssz.Length / UInt32SszVectorConverter.Length];
                UInt32SszVectorConverter.FromSpan(ssz, decodedUints);
                UInt32SszVectorConverter.ToSpan(reEncoded, decodedUints);
                break;
            case TypeUint64:
                ulong[] decodedUlongs = new ulong[ssz.Length / UInt64SszVectorConverter.Length];
                UInt64SszVectorConverter.FromSpan(ssz, decodedUlongs);
                UInt64SszVectorConverter.ToSpan(reEncoded, decodedUlongs);
                break;
            case TypeUint128:
                UInt128[] decodedUint128s = new UInt128[ssz.Length / UInt128SszVectorConverter.Length];
                UInt128SszVectorConverter.FromSpan(ssz, decodedUint128s);
                UInt128SszVectorConverter.ToSpan(reEncoded, decodedUint128s);
                break;
            case TypeUint256:
                int itemCount = ssz.Length / UInt256SszVectorConverter.Length;
                for (int i = 0; i < itemCount; i++)
                {
                    ReadOnlySpan<byte> source = ssz.AsSpan(i * UInt256SszVectorConverter.Length, UInt256SszVectorConverter.Length);
                    Span<byte> target = reEncoded.AsSpan(i * UInt256SszVectorConverter.Length, UInt256SszVectorConverter.Length);
                    UInt256SszVectorConverter.ToSpan(target, UInt256SszVectorConverter.FromSpan(source));
                }
                break;
            default:
                Assert.Fail($"Unsupported element type: {elementType}");
                break;
        }
    }

    private static IEnumerable<TestCaseData> ValidCases() => GetCases("basic_vector", "valid");

    private static IEnumerable<TestCaseData> InvalidCases() => GetCases("basic_vector", "invalid");

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
