// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class SlotRunTests
{
    private static EvmWord Word(int index) => EvmWordSlot.FromUInt256((UInt256)(uint)(index + 1));

    private static byte[] WordBytes(int index)
    {
        EvmWord word = Word(index);
        return EvmWordSlot.AsReadOnlySpan(in word).ToArray();
    }

    private static ISlotRun Build(int count, out ushort mask)
    {
        ISlotRun run = SlotRun.Empty;
        mask = 0;
        for (int index = 0; index < count; index++)
        {
            int slot = SlotRun.Width - 1 - index;
            mask |= (ushort)(1 << slot);
            ISlotRun previous = run;
            run = run.With(slot, Word(slot));
            SlotRun.Return(previous);
        }
        return run;
    }

    [TestCase(0, typeof(EmptySlotRun))]
    [TestCase(1, typeof(SlotRun1))]
    [TestCase(2, typeof(SlotRun2))]
    [TestCase(3, typeof(SlotRun4))]
    [TestCase(4, typeof(SlotRun4))]
    [TestCase(5, typeof(SlotRun8))]
    [TestCase(8, typeof(SlotRun8))]
    [TestCase(9, typeof(SlotRun16))]
    [TestCase(16, typeof(SlotRun16))]
    public void Runs_are_immutable_whole_and_tiered_by_occupancy(int count, Type tier)
    {
        ISlotRun run = Build(count, out ushort mask);
        ISlotRun cleared = run.With(SlotRun.Width - 1, default);
        ISlotRun clone = run.Clone();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run, Is.TypeOf(tier));
            Assert.That(run.Count, Is.EqualTo(count));
            Assert.That(run.Mask, Is.EqualTo(mask));
            Assert.That(Enumerable.Range(0, SlotRun.Width).Select(run.Get), Is.EqualTo(Enumerable.Range(0, SlotRun.Width).Select(index => (mask & (1 << index)) == 0 ? default : Word(index))));
            Assert.That(run.Count, Is.EqualTo(count), "With must not touch its source");
            Assert.That(cleared.Count, Is.EqualTo(Math.Max(count - 1, 0)));
            Assert.That(cleared.Get(SlotRun.Width - 1), Is.EqualTo(default(EvmWord)));
            Assert.That(clone, Is.Not.SameAs(run).Or.SameAs(SlotRun.Empty));
            Assert.That(clone.Mask, Is.EqualTo(mask));
            Assert.That(Enumerable.Range(0, SlotRun.Width).Select(clone.Get), Is.EqualTo(Enumerable.Range(0, SlotRun.Width).Select(run.Get)));
        }
        SlotRun.Return(cleared);
        SlotRun.Return(clone);
        SlotRun.Return(run);
    }

    [TestCase(1, 0x00)]
    [TestCase(2, 0x01)]
    [TestCase(4, 0x02)]
    [TestCase(7, 0x03)]
    [TestCase(16, 0x04)]
    public void Persisted_row_is_type_mask_and_whole_words(int count, byte type)
    {
        ISlotRun run = Build(count, out ushort mask);
        byte[] encoded = new byte[SlotRunCodec.MaxEncodedLength];
        int length = SlotRunCodec.Encode(run, encoded);
        byte[] row = encoded[..length];
        byte[] expected = Bytes.Concat(new byte[] { type, (byte)mask, (byte)(mask >> 8) },
            Bytes.Concat(Enumerable.Range(0, SlotRun.Width).Where(index => (mask & (1 << index)) != 0).Select(WordBytes).ToArray()));
        ISlotRun decoded = SlotRunCodec.Decode(row);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row, Is.EqualTo(expected));
            Assert.That(decoded, Is.TypeOf(run.GetType()));
            Assert.That(Enumerable.Range(0, SlotRun.Width).Select(decoded.Get), Is.EqualTo(Enumerable.Range(0, SlotRun.Width).Select(run.Get)));
            Assert.That(() => SlotRunCodec.Encode(SlotRun.Empty, encoded), Throws.ArgumentException);
            Assert.That(() => SlotRunCodec.Decode(row[..^1]), Throws.TypeOf<System.IO.InvalidDataException>());
            Assert.That(() => SlotRunCodec.Decode(Bytes.Concat(0x05, row[1..])), Throws.TypeOf<System.IO.InvalidDataException>());
        }
        SlotRun.Return(decoded);
        SlotRun.Return(run);
    }

    [TestCase(0u, 0, 64)]
    [TestCase(15u, 15, 64)]
    [TestCase(16u, 0, 80)]
    [TestCase(63u, 15, 112)]
    [TestCase(64u, 0, 64)]
    [TestCase(79u, 15, 64)]
    [TestCase(80u, 0, 80)]
    [TestCase(255u, 15, 240)]
    [TestCase(256u, 0, 0)]
    public void Run_key_clears_the_slot_index_nibble(uint slot, int index, byte runLastByte)
    {
        PbtStorageTreeKey slotKey = PbtStateKey.Storage(TestItem.AddressA, slot);
        PbtStorageTreeKey runKey = SlotRun.RunKey(slotKey);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SlotRun.IndexOf(slotKey), Is.EqualTo(index));
            Assert.That(runKey.Bytes[..^1].ToArray(), Is.EqualTo(slotKey.Bytes[..^1].ToArray()));
            Assert.That(runKey.Bytes[^1], Is.EqualTo(runLastByte));
            Assert.That(SlotRun.IsRunKey(runKey), Is.True);
            Assert.That(SlotRun.IsRunKey(slotKey), Is.EqualTo(index == 0));
            Assert.That(SlotRun.SlotKey(runKey, index), Is.EqualTo(slotKey));
            Assert.That(PbtStateKey.StorageRun(TestItem.AddressA, PbtKeyDerivation.AddressKeyHash(TestItem.AddressA), slot, out int runIndex), Is.EqualTo(runKey));
            Assert.That(runIndex, Is.EqualTo(index));
        }
    }
}
