// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Nethermind.Int256;
using Nethermind.Merkleization;
using NUnit.Framework;
using SszEncoder = Nethermind.Serialization.Ssz.Ssz;

namespace Ethereum.Ssz.Test;

[TestFixture]
public class SszBitTests
{
    // --- Bitvector tests ---

    [TestCaseSource(nameof(BitvectorValidCases))]
    public void Bitvector_valid_roundtrip_and_root(string casePath, int bitLength)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        BitArray decoded = SszEncoder.DecodeBitvector(ssz, bitLength);
        Assert.That(decoded.Length, Is.EqualTo(bitLength));

        int byteLength = (bitLength + 7) / 8;
        byte[] reEncoded = new byte[byteLength];
        SszEncoder.EncodeVector(reEncoded, decoded);
        Assert.That(reEncoded, Is.EqualTo(ssz), "Re-encoded bitvector does not match original SSZ bytes");

        Merkle.Merkleize(out UInt256 actualRoot, (ReadOnlySpan<byte>)ssz);
        Assert.That(actualRoot, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
    }

    [TestCaseSource(nameof(BitvectorInvalidCases))]
    public void Bitvector_invalid_should_fail(string casePath, int bitLength)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        Assert.That(() => SszEncoder.DecodeBitvector(ssz, bitLength), Throws.InstanceOf<Exception>());
    }

    // --- Bitlist tests ---

    [TestCaseSource(nameof(BitlistValidCases))]
    public void Bitlist_valid_roundtrip_and_root(string casePath, int maxBitLength)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        BitArray decoded = SszEncoder.DecodeBitlist(ssz);
        Assert.That(decoded.Length, Is.LessThanOrEqualTo(maxBitLength), "Decoded bitlist length exceeds the limit");

        int encodedByteLength = (decoded.Length + 8) / 8;
        byte[] reEncoded = new byte[encodedByteLength];
        SszEncoder.EncodeList(reEncoded, decoded);
        Assert.That(reEncoded, Is.EqualTo(ssz), "Re-encoded bitlist does not match original SSZ bytes");

        uint chunkLimit = (uint)((maxBitLength + 255) / 256);
        byte[] sszCopy = (byte[])ssz.Clone();
        Merkle.MerkleizeBits(out UInt256 actualRoot, sszCopy, chunkLimit);
        Assert.That(actualRoot, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
    }

    [TestCaseSource(nameof(BitlistInvalidCases))]
    public void Bitlist_invalid_should_fail(string casePath, int maxBitLength)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));

        bool isInvalid = false;

        if (ssz.Length == 0)
        {
            isInvalid = true;
        }
        else if (ssz[^1] == 0)
        {
            isInvalid = true;
        }
        else
        {
            BitArray decoded = SszEncoder.DecodeBitlist(ssz);
            if (decoded.Length > maxBitLength)
            {
                isInvalid = true;
            }
        }

        Assert.That(isInvalid, Is.True, "Expected invalid bitlist but it appears valid");
    }

    // --- Helpers ---


    /// <summary>
    /// Extracts the bit length/limit from the case name.
    /// Case names look like "bitvec_16_random" or "bitlist_512_lengthy_0".
    /// The number after the first prefix is the bit length/limit.
    /// </summary>
    private static int ExtractBitSize(string caseName, string prefix)
    {
        // Strip prefix: "bitvec_16_random" → "16_random"
        string rest = caseName.Substring(prefix.Length);
        int underscoreIdx = rest.IndexOf('_');
        string numberPart = underscoreIdx >= 0 ? rest.Substring(0, underscoreIdx) : rest;
        return int.Parse(numberPart);
    }

    // --- Test case sources ---
    // Structure: handler/valid/{case_name}/ and handler/invalid/{case_name}/
    // Case names: "bitvec_{N}_{descriptor}" for bitvectors, "bitlist_{N}_{descriptor}" for bitlists

    private static IEnumerable<TestCaseData> BitvectorValidCases()
    {
        return GetBitCases("bitvector", "valid", "bitvec_");
    }

    private static IEnumerable<TestCaseData> BitvectorInvalidCases()
    {
        return GetBitCases("bitvector", "invalid", "bitvec_");
    }

    private static IEnumerable<TestCaseData> BitlistValidCases()
    {
        return GetBitCases("bitlist", "valid", "bitlist_");
    }

    private static IEnumerable<TestCaseData> BitlistInvalidCases()
    {
        return GetBitCases("bitlist", "invalid", "bitlist_");
    }

    private static IEnumerable<TestCaseData> GetBitCases(string handler, string validity, string casePrefix)
    {
        string handlerPath = SszConsensusTestLoader.GetHandlerPath(handler);
        string validityPath = Path.Combine(handlerPath, validity);
        if (!Directory.Exists(validityPath))
            yield break;

        foreach (string casePath in Directory.GetDirectories(validityPath))
        {
            string caseName = Path.GetFileName(casePath);
            int bitSize = ExtractBitSize(caseName, casePrefix);
            yield return new TestCaseData(casePath, bitSize)
                .SetName($"{handler}/{validity}/{caseName}");
        }
    }
}
