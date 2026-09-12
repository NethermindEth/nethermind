// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SlotValueTests
{
    private const string FullSlotHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

    [Test]
    public void UInt256_conversion_preserves_value_and_big_endian_encoding(
        [Values("00", "01", "7f", "80", "ff", "0100", "0de0b6b3a7640000", FullSlotHex)] string hex)
    {
        UInt256 expected = new(Bytes.FromHexString(hex), isBigEndian: true);
        SlotValue slot = new(in expected);
        slot.ToUInt256(out UInt256 actual);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(slot.Value.ToBigEndian(), Is.EqualTo(expected.ToBigEndian()));
        }
    }

    private static byte[] IncrementingBytes(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i + 1);
        return data;
    }

    [Test]
    public void Test_Ctor_AcceptsLengthsUpTo32([Values(0, 1, 16, 31, 32)] int length)
    {
        byte[] data = IncrementingBytes(length);

        SlotValue value = new(data);
        ReadOnlySpan<byte> bytes = value.Value.ToBigEndian().AsSpan();

        Assert.That(bytes.Length, Is.EqualTo(32));
        for (int i = 0; i < length; i++) Assert.That(bytes[i], Is.EqualTo(data[i]));
        for (int i = length; i < 32; i++) Assert.That(bytes[i], Is.EqualTo(0));
    }

    [Test]
    public void Test_Ctor_ThrowsOnOversizedInput() =>
        Assert.That(() => new SlotValue(new byte[33]), Throws.ArgumentException);

    [Test]
    public void Test_FromSpanWithoutLeadingZero_ThrowsOnOversizedInput([Values(33, 64)] int length) =>
        Assert.That(() => SlotValue.FromSpanWithoutLeadingZero(new byte[length]), Throws.ArgumentException);

    [Test]
    public void Test_FromSpanWithoutLeadingZero_FastPath_For32Bytes()
    {
        byte[] data = Bytes.FromHexString(FullSlotHex);
        SlotValue value = SlotValue.FromSpanWithoutLeadingZero(data);
        Assert.That(value.Value.ToBigEndian(), Is.EqualTo(data));
    }

    [Test]
    public void Test_FromSpanWithoutLeadingZero_PadsLeadingZeros([Values(0, 1, 16, 31)] int length)
    {
        byte[] data = IncrementingBytes(length);

        SlotValue value = SlotValue.FromSpanWithoutLeadingZero(data);
        ReadOnlySpan<byte> bytes = value.Value.ToBigEndian().AsSpan();

        for (int i = 0; i < 32 - length; i++) Assert.That(bytes[i], Is.EqualTo(0));
        for (int i = 0; i < length; i++) Assert.That(bytes[32 - length + i], Is.EqualTo(data[i]));
    }

    [Test]
    public void Test_FromBytes_ReturnsNullForNull() =>
        Assert.That(SlotValue.FromBytes(null), Is.Null);

    [Test]
    public void NullableSlotValue_IsCompact() =>
        Assert.That(Unsafe.SizeOf<SlotValue?>(), Is.EqualTo(40));

    [Test]
    public void Test_FromBytes_WrapsNonNull()
    {
        byte[] data = Bytes.FromHexString(FullSlotHex);
        SlotValue? value = SlotValue.FromBytes(data);
        Assert.That(value, Is.Not.Null);
        Assert.That(value!.Value.Value.ToBigEndian(), Is.EqualTo(data));
    }

}
