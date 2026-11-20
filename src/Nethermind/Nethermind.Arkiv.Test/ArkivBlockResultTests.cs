// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Arkiv.Native;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Arkiv.Test;

public class ArkivBlockResultTests
{
    [Test]
    public void Decodes_the_native_layout()
    {
        List<byte> bytes = [];
        bytes.AddRange(TestItem.KeccakA.Bytes.ToArray());
        bytes.AddRange(U64(52_000));
        bytes.Add(1);
        bytes.AddRange(U32(2));
        // A successful receipt with one log of two topics, and a failed one without logs.
        bytes.Add(1); bytes.AddRange(U64(21_830)); bytes.AddRange(U32(1));
        bytes.AddRange(TestItem.AddressA.Bytes); bytes.AddRange(U32(2)); bytes.AddRange(TestItem.KeccakB.Bytes.ToArray()); bytes.AddRange(TestItem.KeccakC.Bytes.ToArray());
        bytes.AddRange(U32(3)); bytes.AddRange(new byte[] { 1, 2, 3 });
        bytes.Add(0); bytes.AddRange(U64(52_000)); bytes.AddRange(U32(0));
        bytes.AddRange(U32(1));
        bytes.AddRange(TestItem.AddressB.Bytes); bytes.AddRange(((UInt256)1_000_000).ToBigEndian()); bytes.AddRange(U64(7));
        bytes.AddRange(U32(2));
        bytes.AddRange(TestItem.KeccakD.Bytes.ToArray()); bytes.AddRange(I32(2)); bytes.AddRange("{}"u8.ToArray());
        bytes.AddRange(TestItem.KeccakE.Bytes.ToArray()); bytes.AddRange(I32(-1));

        ArkivBlockResult result = ArkivBlockResult.Decode(bytes.ToArray());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateRoot, Is.EqualTo(TestItem.KeccakA));
            Assert.That(result.GasUsed, Is.EqualTo(52_000));
            Assert.That(result.RequestsEmpty, Is.True);
            Assert.That(result.Receipts, Has.Length.EqualTo(2));
            Assert.That(result.Receipts[0].Logs[0].Topics, Is.EqualTo(new[] { TestItem.KeccakB, TestItem.KeccakC }));
            Assert.That(result.Receipts[0].Logs[0].Data, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(result.Receipts[1].Success, Is.False);
            Assert.That(result.Accounts, Is.EqualTo(new[] { new ArkivAccount(TestItem.AddressB, 1_000_000, 7) }));
            Assert.That(result.Entities[0].Json, Is.EqualTo("{}"u8.ToArray()));
            Assert.That(result.Entities[1].Json, Is.Null);
        }
    }

    [Test]
    public void Refuses_a_truncated_result() =>
        Assert.Throws<FormatException>(() => ArkivBlockResult.Decode(new byte[40]));

    private static byte[] U64(ulong v) { byte[] b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }
    private static byte[] U32(uint v) { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    private static byte[] I32(int v) { byte[] b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return b; }
}
