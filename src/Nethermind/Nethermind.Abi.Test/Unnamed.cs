// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Unnamed
{
    [Test]
    public void NestedPayload()
    {
        Address batcher = new("0x5a4eab120fb44eb6684e5e32785702ff45ea344d");
        byte[] data = Bytes.FromHexString(
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to `data`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Length of `data`
            "0000000000000000000000005a4eab120fb44eb6684e5e32785702ff45ea344d"); // Contents of `data`, `abi.encode(batcher)`

        byte[] inner = AbiCodec.Decode(new AbiSignature().With(AbiType.Bytes), data);
        Address decoded = AbiCodec.Decode(new AbiSignature().With(AbiType.Address), inner);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner, Is.EqualTo(data[64..]));
            Assert.That(decoded, Is.EqualTo(batcher));
        }
    }

    private static IEnumerable<TestCaseData> NamedTestCases()
    {
        yield return Named(static s => s.With(AbiType.UInt32, AbiType.Bool), (69u, true));
        yield return Named(static s => s.With(AbiType.String), "hello");
        yield return Named(static s => s.With(AbiType.Bytes, AbiType.Bool, AbiType.Array(AbiType.UInt)), ("dave"u8.ToArray(), true, [1, 2, 3]));
        yield return Named(static s => s.With(), default);
    }

    private static TestCaseData Named<T>(Func<AbiSignature, AbiSignature<T>> with, T value)
    {
        AbiSignature<T> unnamed = with(new AbiSignature());
        return new TestCaseData(unnamed, with(new AbiSignature("f")), value).SetArgDisplayNames(unnamed.ToString());
    }

    [TestCaseSource(nameof(NamedTestCases))]
    public void MatchesNamedWithoutMethodId<T>(AbiSignature<T> unnamed, AbiSignature<T> named, T value)
    {
        byte[] encoded = AbiCodec.Encode(unnamed, value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unnamed.MethodId.ToArray(), Is.Empty);
            Assert.That(encoded, Is.EqualTo(AbiCodec.Encode(named, value)[AbiSignature.MethodIdLength..]));
            Assert.That(unnamed.Size(value), Is.EqualTo(named.Size(value) - AbiSignature.MethodIdLength));
            Assert.That(AbiCodec.Decode(unnamed, encoded), Is.EqualTo(value));
        }
    }

    [Test]
    public void EmptyName_HasMethodId() =>
        Assert.That(new AbiSignature("").With().MethodId.ToArray(), Is.EqualTo(Bytes.FromHexString("861731d5"))); // `keccak256("()")`
}
