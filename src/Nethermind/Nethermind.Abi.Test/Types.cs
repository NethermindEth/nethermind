// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Types
{
    [Test]
    public void TupleName()
    {
        Abi<(byte, ulong)> tuple = AbiType.Tuple(AbiType.UInt8, AbiType.UInt64);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tuple.Name, Is.EqualTo("(uint8,uint64)"));
            Assert.That(AbiType.Array(tuple).Name, Is.EqualTo("(uint8,uint64)[]"));
        }
    }
}
