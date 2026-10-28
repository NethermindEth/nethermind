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

    [Test]
    public void Serves_every_code_through_slot_collisions_replacement_and_growth([Values] bool sameSlot)
    {
        GuestCodeCache cache = new(4);
        ValueHash256[] hashes = new ValueHash256[100];
        CodeInfo[] codes = new CodeInfo[hashes.Length];
        for (int i = 0; i < hashes.Length; i++)
        {
            byte[] bytes = new byte[32];
            bytes[sameSlot ? 31 : 0] = (byte)(i + 1);
            hashes[i] = new ValueHash256(bytes);
            codes[i] = new CodeInfo(new byte[] { (byte)Instruction.STOP });
            cache.Set(in hashes[i], codes[i]);
        }

        CodeInfo replacement = new(new byte[] { (byte)Instruction.STOP });
        cache.Set(in hashes[7], replacement);
        codes[7] = replacement;

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < hashes.Length; i++)
            {
                Assert.That(cache.Get(in hashes[i]), Is.SameAs(codes[i]));
            }

            Assert.That(cache.Get(in Absent), Is.Null);
        }
    }
}
