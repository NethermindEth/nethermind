// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Integers
{
    private static IEnumerable<TestCaseData> NameTestCases()
    {
        yield return Case(AbiType.UInt8, "uint8");
        yield return Case(AbiType.UInt16, "uint16");
        yield return Case(AbiType.UInt32, "uint32");
        yield return Case(AbiType.UInt64, "uint64");
        yield return Case(AbiType.UInt256, "uint256");
        yield return Case(AbiType.Int8, "int8");
        yield return Case(AbiType.Int16, "int16");
        yield return Case(AbiType.Int32, "int32");
        yield return Case(AbiType.Int64, "int64");
        yield return Case(AbiType.Int256, "int256");
        yield return Case(AbiType.UIntN<UInt128>(), "uint128");
        yield return Case(AbiType.IntN<Int128>(), "int128");
    }

    private static TestCaseData Case<T>(Abi<T> abi, string name) =>
        new TestCaseData(abi, name).SetArgDisplayNames(name);

    [TestCaseSource(nameof(NameTestCases))]
    public void Name<T>(Abi<T> abi, string name) =>
        Assert.That(abi.Name, Is.EqualTo(name));

    private static IEnumerable<TestCaseData> MethodIdTestCases()
    {
        yield return new TestCaseData(AbiType.Int8, "0a9a2963").SetName("f(int8)");
        yield return new TestCaseData(AbiType.Int, "1c008df9").SetName("f(int256)");
    }

    [TestCaseSource(nameof(MethodIdTestCases))]
    public void MethodId<T>(Abi<T> abi, string expectedMethodId) =>
        Assert.That(new AbiSignature("f").With(abi).MethodId.ToArray(), Is.EqualTo(Bytes.FromHexString(expectedMethodId)));
}
