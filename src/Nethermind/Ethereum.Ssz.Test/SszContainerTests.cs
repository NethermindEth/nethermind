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
    [TestCaseSource(nameof(ValidContainerCases))]
    public void Container_valid_roundtrip_and_root(string casePath, string containerType)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        switch (containerType)
        {
            case "SingleFieldTestStruct":
                {
                    SszEncoding.Decode(ssz, out SingleFieldTestStruct decoded);
                    byte[] reEncoded = SszEncoding.Encode(decoded);
                    Assert.That(reEncoded, Is.EqualTo(ssz), "Re-encoded SSZ does not match original");
                    SszEncoding.Merkleize(decoded, out UInt256 root);
                    Assert.That(root, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
                    break;
                }
            case "SmallTestStruct":
                {
                    SszEncoding.Decode(ssz, out SmallTestStruct decoded);
                    byte[] reEncoded = SszEncoding.Encode(decoded);
                    Assert.That(reEncoded, Is.EqualTo(ssz), "Re-encoded SSZ does not match original");
                    SszEncoding.Merkleize(decoded, out UInt256 root);
                    Assert.That(root, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
                    break;
                }
            case "FixedTestStruct":
                {
                    SszEncoding.Decode(ssz, out FixedTestStruct decoded);
                    byte[] reEncoded = SszEncoding.Encode(decoded);
                    Assert.That(reEncoded, Is.EqualTo(ssz), "Re-encoded SSZ does not match original");
                    SszEncoding.Merkleize(decoded, out UInt256 root);
                    Assert.That(root, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
                    break;
                }
            case "VarTestStruct":
                {
                    SszEncoding.Decode(ssz, out VarTestStruct decoded);
                    byte[] reEncoded = SszEncoding.Encode(decoded);
                    Assert.That(reEncoded, Is.EqualTo(ssz), "Re-encoded SSZ does not match original");
                    SszEncoding.Merkleize(decoded, out UInt256 root);
                    Assert.That(root, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
                    break;
                }
            case "ComplexTestStruct":
                {
                    SszEncoding.Decode(ssz, out ComplexTestStruct decoded);
                    byte[] reEncoded = SszEncoding.Encode(decoded);
                    Assert.That(reEncoded, Is.EqualTo(ssz), "Re-encoded SSZ does not match original");
                    SszEncoding.Merkleize(decoded, out UInt256 root);
                    Assert.That(root, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
                    break;
                }
            case "BitsStruct":
                {
                    SszEncoding.Decode(ssz, out BitsStruct decoded);
                    byte[] reEncoded = SszEncoding.Encode(decoded);
                    Assert.That(reEncoded, Is.EqualTo(ssz), "Re-encoded SSZ does not match original");
                    SszEncoding.Merkleize(decoded, out UInt256 root);
                    Assert.That(root, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
                    break;
                }
            case "ProgressiveTestStruct":
                {
                    SszEncoding.Decode(ssz, out ProgressiveTestStruct decoded);
                    byte[] reEncoded = SszEncoding.Encode(decoded);
                    Assert.That(reEncoded, Is.EqualTo(ssz), "Re-encoded SSZ does not match original");
                    SszEncoding.Merkleize(decoded, out UInt256 root);
                    Assert.That(root, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
                    break;
                }
            case "ProgressiveBitsStruct":
                {
                    SszEncoding.Decode(ssz, out ProgressiveBitsStruct decoded);
                    byte[] reEncoded = SszEncoding.Encode(decoded);
                    Assert.That(reEncoded, Is.EqualTo(ssz), "Re-encoded SSZ does not match original");
                    SszEncoding.Merkleize(decoded, out UInt256 root);
                    Assert.That(root, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
                    break;
                }
            default:
                Assert.Fail($"Unrecognized container type: {containerType} - add test support for it in {nameof(SszContainerTests)}");
                break;
        }
    }

    [TestCaseSource(nameof(InvalidContainerCases))]
    public void Container_invalid_should_fail(string casePath, string containerType)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));

        switch (containerType)
        {
            case "SingleFieldTestStruct":
                Assert.That(() => SszEncoding.Decode(ssz, out SingleFieldTestStruct _), Throws.InstanceOf<Exception>());
                break;
            case "SmallTestStruct":
                Assert.That(() => SszEncoding.Decode(ssz, out SmallTestStruct _), Throws.InstanceOf<Exception>());
                break;
            case "FixedTestStruct":
                Assert.That(() => SszEncoding.Decode(ssz, out FixedTestStruct _), Throws.InstanceOf<Exception>());
                break;
            case "VarTestStruct":
                Assert.That(() => SszEncoding.Decode(ssz, out VarTestStruct _), Throws.InstanceOf<Exception>());
                break;
            case "ComplexTestStruct":
                Assert.That(() => SszEncoding.Decode(ssz, out ComplexTestStruct _), Throws.InstanceOf<Exception>());
                break;
            case "BitsStruct":
                Assert.That(() => SszEncoding.Decode(ssz, out BitsStruct _), Throws.InstanceOf<Exception>());
                break;
            case "ProgressiveTestStruct":
                Assert.That(() => SszEncoding.Decode(ssz, out ProgressiveTestStruct _), Throws.InstanceOf<Exception>());
                break;
            case "ProgressiveBitsStruct":
                Assert.That(() => SszEncoding.Decode(ssz, out ProgressiveBitsStruct _), Throws.InstanceOf<Exception>());
                break;
            default:
                Assert.Fail($"Unrecognized container type: {containerType} - add test support for it in {nameof(SszContainerTests)}");
                break;
        }
    }

    // --- Helpers ---


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
