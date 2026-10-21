// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Arities
{
    [Test]
    public void ZeroArguments()
    {
        AbiSignature<ValueTuple> signature = new AbiSignature("getCoreState").With();
        byte[] methodId = Bytes.FromHexString("6aa6a01a");
        byte[] otherMethodId = Bytes.FromHexString("c3f909d4"); // `getConfig()`

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AbiType.Tuple().Name, Is.EqualTo("()"));
            Assert.That(signature.MethodId.ToArray(), Is.EqualTo(methodId));
            Assert.That(AbiCodec.Encode(signature, default), Is.EqualTo(methodId));
            Assert.DoesNotThrow(() => AbiCodec.Decode(signature, methodId));
            Assert.Throws<AbiException>(() => AbiCodec.Decode(signature, Bytes.Concat(methodId, new byte[32])));
            Assert.Throws<AbiException>(() => AbiCodec.Decode(signature, otherMethodId));
        }
    }
}
