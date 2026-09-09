// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;

namespace Ethereum.Ssz.Test;

[TestFixture]
public class SszContainerTests
{
    private interface IContainerHandler
    {
        void RunValid(byte[] ssz, UInt256 expectedRoot);
        void RunInvalid(byte[] ssz);
    }

    private sealed class ContainerHandler<T> : IContainerHandler where T : ISszCodec<T>
    {
        public void RunValid(byte[] ssz, UInt256 expectedRoot)
        {
            T.Decode(ssz, out T decoded);
            Assert.That(T.Encode(decoded), Is.EqualTo(ssz), "Re-encoded SSZ does not match original");

            T.Merkleize(decoded, out UInt256 root);
            Assert.That(root, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
        }

        public void RunInvalid(byte[] ssz) =>
            Assert.That(() => T.Decode(ssz, out T _), Throws.InstanceOf<InvalidDataException>());
    }

    private sealed class HandlerMap() : Dictionary<string, IContainerHandler>(StringComparer.Ordinal)
    {
        public HandlerMap Add<T>() where T : ISszCodec<T>
        {
            this[typeof(T).Name] = new ContainerHandler<T>();
            return this;
        }
    }

    private static readonly IReadOnlyDictionary<string, IContainerHandler> Handlers = new HandlerMap()
        .Add<SingleFieldTestStruct>()
        .Add<SmallTestStruct>()
        .Add<FixedTestStruct>()
        .Add<VarTestStruct>()
        .Add<ComplexTestStruct>()
        .Add<BitsStruct>()
        .Add<ProgressiveTestStruct>()
        .Add<ProgressiveBitsStruct>();

    [TestCaseSource(nameof(ValidContainerCases))]
    public void Container_valid_roundtrip_and_root(string casePath, string containerType)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        UInt256 expectedRoot = SszConsensusTestLoader.ParseRoot(Path.Combine(casePath, "meta.yaml"));

        Assert.That(Handlers.TryGetValue(containerType, out IContainerHandler? handler), Is.True,
            $"Unrecognized container type: {containerType} - add test support for it in {nameof(SszContainerTests)}");
        handler!.RunValid(ssz, expectedRoot);
    }

    [TestCaseSource(nameof(InvalidContainerCases))]
    public void Container_invalid_should_fail(string casePath, string containerType)
    {
        byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));

        Assert.That(Handlers.TryGetValue(containerType, out IContainerHandler? handler), Is.True,
            $"Unrecognized container type: {containerType} - add test support for it in {nameof(SszContainerTests)}");
        handler!.RunInvalid(ssz);
    }

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
