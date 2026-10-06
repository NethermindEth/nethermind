// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

public class GuestCodeCacheTests
{
    private static readonly ValueHash256 Stored = new("0x1c4a6a8d24f1b3c5e7091b2d4f6a8c0e2a4c6e8f0b2d4f6a8c0e2a4c6e8f0b2d");
    private static readonly ValueHash256 Absent = new("0x2c4a6a8d24f1b3c5e7091b2d4f6a8c0e2a4c6e8f0b2d4f6a8c0e2a4c6e8f0b2d");

    [Test]
    public void Serves_what_was_set_under_its_hash_and_stamps_it()
    {
        GuestCodeCache cache = new(4);
        CodeInfo codeInfo = new(new byte[] { (byte)Instruction.STOP });

        cache.Set(in Stored, codeInfo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Get(in Stored), Is.SameAs(codeInfo));
            Assert.That(cache.Get(in Absent), Is.Null);
            Assert.That(codeInfo.CodeHash, Is.EqualTo(Stored));
        }

        cache.Clear();
        Assert.That(cache.Get(in Stored), Is.Null);
    }
}
