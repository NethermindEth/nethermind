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
    private static readonly HashSet<string> KnownContainers =
    [
        "SingleFieldTestStruct",
        "SmallTestStruct",
        "FixedTestStruct",
        "VarTestStruct",
        "ComplexTestStruct",
        "BitsStruct"
    ];

    // These must be listed explicitly. If the specs add a new type
    // then we should add it here if its not implemented or the build will fail.
    private static readonly HashSet<string> UnsupportedContainers =
    [
        "ProgressiveTestStruct",
        "ProgressiveBitsStruct"
    ];

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
            default:
                if (UnsupportedContainers.Contains(containerType))
                    Assert.Ignore($"Unsupported container type (not yet implemented): {containerType}");
                else
                    Assert.Fail($"Unrecognized container type: {containerType} — add it to KnownContainers or UnsupportedContainers");
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
            default:
                if (UnsupportedContainers.Contains(containerType))
                    Assert.Ignore($"Unsupported container type (not yet implemented): {containerType}");
                else
                    Assert.Fail($"Unrecognized container type: {containerType} — add it to KnownContainers or UnsupportedContainers");
                break;
        }
    }

    // --- Helpers ---


    /// <summary>
    /// Extracts the container type from a case name like "BitsStruct_lengthy_0" → "BitsStruct".
    /// Matches against both known and explicitly unsupported types.
    /// Returns null only for truly unrecognized types (which should fail the test).
    /// </summary>
    private static string? ExtractContainerType(string caseName)
    {
        foreach (string container in KnownContainers)
        {
            if (caseName.StartsWith(container, StringComparison.Ordinal))
                return container;
        }
        foreach (string container in UnsupportedContainers)
        {
            if (caseName.StartsWith(container, StringComparison.Ordinal))
                return container;
        }
        return null;
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
            string? containerType = ExtractContainerType(caseName);

            // Unrecognized type — yield it so the test fails loudly
            yield return new TestCaseData(casePath, containerType ?? caseName)
                .SetName($"containers/{validity}/{caseName}");
        }
    }
}
