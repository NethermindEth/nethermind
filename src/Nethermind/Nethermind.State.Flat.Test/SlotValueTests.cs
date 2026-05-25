// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SlotValueTests
{
    private const string FullSlotHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

    private static byte[] IncrementingBytes(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i + 1);
        return data;
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(16)]
    [TestCase(31)]
    [TestCase(32)]
    public void Test_Ctor_AcceptsLengthsUpTo32(int length)
    {
        byte[] data = IncrementingBytes(length);

        SlotValue value = new(data);
        ReadOnlySpan<byte> bytes = value.AsReadOnlySpan;

        Assert.That(bytes.Length, Is.EqualTo(32));
        for (int i = 0; i < length; i++) Assert.That(bytes[i], Is.EqualTo(data[i]));
        for (int i = length; i < 32; i++) Assert.That(bytes[i], Is.EqualTo(0));
    }

    [Test]
    public void Test_Ctor_ThrowsOnOversizedInput() =>
        Assert.That(() => new SlotValue(new byte[33]), Throws.ArgumentException);

    [TestCase(33)]
    [TestCase(64)]
    public void Test_FromSpanWithoutLeadingZero_ThrowsOnOversizedInput(int length) =>
        Assert.That(() => SlotValue.FromSpanWithoutLeadingZero(new byte[length]), Throws.ArgumentException);

    [Test]
    public void Test_FromSpanWithoutLeadingZero_FastPath_For32Bytes()
    {
        byte[] data = Bytes.FromHexString(FullSlotHex);
        SlotValue value = SlotValue.FromSpanWithoutLeadingZero(data);
        Assert.That(value.AsReadOnlySpan.ToArray(), Is.EqualTo(data));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(16)]
    [TestCase(31)]
    public void Test_FromSpanWithoutLeadingZero_PadsLeadingZeros(int length)
    {
        byte[] data = IncrementingBytes(length);

        SlotValue value = SlotValue.FromSpanWithoutLeadingZero(data);
        ReadOnlySpan<byte> bytes = value.AsReadOnlySpan;

        for (int i = 0; i < 32 - length; i++) Assert.That(bytes[i], Is.EqualTo(0));
        for (int i = 0; i < length; i++) Assert.That(bytes[32 - length + i], Is.EqualTo(data[i]));
    }

    [Test]
    public void Test_FromBytes_ReturnsNullForNull() =>
        Assert.That(SlotValue.FromBytes(null), Is.Null);

    [Test]
    public void Test_FromBytes_WrapsNonNull()
    {
        byte[] data = Bytes.FromHexString(FullSlotHex);
        SlotValue? value = SlotValue.FromBytes(data);
        Assert.That(value, Is.Not.Null);
        Assert.That(value!.Value.AsReadOnlySpan.ToArray(), Is.EqualTo(data));
    }

    [Test]
    public void Test_ToEvmBytes_ReturnsCanonicalZeroForZeroValue()
    {
        SlotValue zero = new(ReadOnlySpan<byte>.Empty);
        Assert.That(zero.ToEvmBytes(), Is.EqualTo(new byte[] { 0 }));
    }

    [TestCase("01", "01")]
    [TestCase("0102", "0102")]
    [TestCase("00ff", "ff")]
    [TestCase(FullSlotHex, FullSlotHex)]
    public void Test_ToEvmBytes_StripsLeadingZerosForNonZeroValue(string inputHex, string expectedHex)
    {
        SlotValue value = SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString(inputHex));
        Assert.That(value.ToEvmBytes(), Is.EqualTo(Bytes.FromHexString(expectedHex)));
    }
}
