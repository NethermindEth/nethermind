// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Extensions;
using NUnit.Framework;
using Paprika.Data;

namespace Paprika.Tests.Data;

public class NibblePathTests
{
    [Test]
    public void Equal_From([Range(0, 15)] int from)
    {
        const ulong value = 0xFE_DC_BA_98_76_54_32_10;
        Span<byte> span = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);

        NibblePath path = NibblePath.FromKey(span, from);

        Span<byte> destination = stackalloc byte[path.MaxByteLength];
        path.WriteToWithLeftover(destination);

        NibblePath.ReadFrom(destination, out NibblePath parsed);

        Assert.That(parsed.Equals(path), Is.True);
    }

    [Test]
    public void Get_at_even()
    {
        const byte first = 0xDC;
        const byte second = 0xBA;
        Span<byte> span = [first, second];

        NibblePath path = NibblePath.FromKey(span);

        Span<byte> expected = [0xD, 0xC, 0xB, 0xA];

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(path.GetAt(i), Is.EqualTo(expected[i]));
        }
    }

    [Test]
    public void Get_at_odd()
    {
        const byte first = 0xDC;
        const byte second = 0xBA;
        const byte third = 0x90;
        Span<byte> span = [first, second, third];

        NibblePath path = NibblePath.FromKey(span, 1);

        Span<byte> expected = [0xC, 0xB, 0xA, 0x9];

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(path.GetAt(i), Is.EqualTo(expected[i]));
        }
    }

    private static void AssertDiffAt(in NibblePath path1, in NibblePath path2, int diffAt)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(path1.FindFirstDifferentNibble(path2), Is.EqualTo(diffAt));
            Assert.That(path2.FindFirstDifferentNibble(path1), Is.EqualTo(diffAt));
        }
    }

    [Test]
    public void FindFirstDifferent_equal_even_even()
    {
        NibblePath path1 = NibblePath.FromKey(Bytes.FromHexString("0xDCBA"));
        NibblePath path2 = NibblePath.FromKey(Bytes.FromHexString("0xDCBA"));

        AssertDiffAt(path1, path2, 4);
    }

    [Test]
    public void FindFirstDifferent_diff_even_even()
    {
        NibblePath path1 = NibblePath.FromKey(Bytes.FromHexString("0xD1BA"));
        NibblePath path2 = NibblePath.FromKey(Bytes.FromHexString("0xDCBA"));

        AssertDiffAt(path1, path2, 1);
    }

    [Test]
    public void FindFirstDifferent_equal_even_odd()
    {
        NibblePath path1 = NibblePath.FromKey(Bytes.FromHexString("0xDCBA"));
        NibblePath path2 = NibblePath.FromKey(Bytes.FromHexString("0x0DCBA0"), 1).SliceTo(path1.Length);

        AssertDiffAt(path1, path2, 4);
    }

    [Test]
    public void FindFirstDifferent_diff_even_odd()
    {
        NibblePath path1 = NibblePath.FromKey(Bytes.FromHexString("0xDC1A"));
        NibblePath path2 = NibblePath.FromKey(Bytes.FromHexString("0x0DCBA0"), 1).SliceTo(path1.Length);

        AssertDiffAt(path1, path2, 2);
    }

    [Test]
    public void FindFirstDifferent_diff_odd_odd()
    {
        NibblePath path1 = NibblePath.FromKey(Bytes.FromHexString("0x0DCBA0"), 1).SliceTo(4);
        NibblePath path2 = NibblePath.FromKey(Bytes.FromHexString("0x0D1BA0"), 1).SliceTo(4);

        AssertDiffAt(path1, path2, 1);
    }

    [Test]
    public void FindFirstDifferent_diff_at_0__odd_odd()
    {
        NibblePath path1 = NibblePath.FromKey(Bytes.FromHexString("0x0DCBA0"), 1).SliceTo(4);
        NibblePath path2 = NibblePath.FromKey(Bytes.FromHexString("0x01CBA0"), 1).SliceTo(4);

        AssertDiffAt(path1, path2, 0);
    }

    [Test]
    public void FindFirstDifferent_diff_at_last_long_even_even()
    {
        NibblePath path1 = NibblePath.FromKey(Bytes.FromHexString("0x123456789ABCDE2456AC"));
        NibblePath path2 = NibblePath.FromKey(Bytes.FromHexString("0x123456789ABCDE2456A1"));

        AssertDiffAt(path1, path2, 19);
    }

    [Test]
    public void FindFirstDifferent_diff_at_1_long_even_even()
    {
        NibblePath path1 = NibblePath.FromKey(Bytes.FromHexString("0xA23456789ABCDE2456AC"));
        NibblePath path2 = NibblePath.FromKey(Bytes.FromHexString("0xAD3456789ABCDE2456AC"));

        AssertDiffAt(path1, path2, 1);
    }

    [Test]
    public void SliceFrom([Values(0, 2, 4, 6, 8, 10, 12, 14)] int sliceFrom)
    {
        const int length = 8;
        const ulong value = 0xFE_DC_BA_98_76_54_32_10;
        Span<byte> span = stackalloc byte[length];
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);

        NibblePath original = NibblePath.FromKey(span, 0);
        NibblePath sliced = original.SliceFrom(sliceFrom);
        NibblePath expected = NibblePath.FromKey(span.Slice(sliceFrom / 2), 0);

        Assert.That(expected.Equals(sliced), Is.True);
    }

    [Test]
    public void ToString_even_even()
    {
        NibblePath path = NibblePath.FromKey(Bytes.FromHexString("0x12345678"));
        Assert.That(path.ToString(), Is.EqualTo("12345678"));
    }

    [Test]
    public void ToString_odd_odd()
    {
        NibblePath path = NibblePath.FromKey(Bytes.FromHexString("0x12345678"), 1);
        Assert.That(path.ToString(), Is.EqualTo("2345678"));
    }

    [Test]
    public void ToString_odd_even()
    {
        NibblePath path = NibblePath.FromKey(Bytes.FromHexString("0x12345678"), 1).SliceTo(6);
        Assert.That(path.ToString(), Is.EqualTo("234567"));
    }

    [Test]
    public void ToString_even_odd()
    {
        NibblePath path = NibblePath.FromKey(Bytes.FromHexString("0x12345678")).SliceTo(7);
        Assert.That(path.ToString(), Is.EqualTo("1234567"));
    }

    [TestCase(0, false, 1)]
    [TestCase(3, false, 4)]
    [TestCase(0, true, 2)]
    [TestCase(3, true, 5)]
    public void NibbleAt(int at, bool odd, byte result)
    {
        NibblePath path = NibblePath.FromKey(Bytes.FromHexString("0x123456"), odd ? 1 : 0);
        Assert.That(path.GetAt(at), Is.EqualTo(result));
    }

    [TestCase(0, 4)]
    [TestCase(0, 3)]
    [TestCase(1, 3)]
    [TestCase(1, 2)]
    public void Write_and_read(int from, int length)
    {
        const byte data = 253;
        NibblePath path = NibblePath.FromKey(Bytes.FromHexString("0x1234")).SliceFrom(from).SliceTo(length);

        Span<byte> span = stackalloc byte[path.MaxByteLength + 1];
        Span<byte> written = path.WriteTo(span);
        span[written.Length] = data;

        Span<byte> left = NibblePath.ReadFrom(span.Slice(0, written.Length + 1), out NibblePath actual);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.ToString(), Is.EqualTo(path.ToString()));
            Assert.That(left.Length, Is.EqualTo(1));
            Assert.That(left[0], Is.EqualTo(data));
        }
    }

    [Test]
    public void Writing_path_with_last_nibble_skipped_should_not_care_about_the_rest()
    {
        const int length = 3;

        NibblePath pathA = NibblePath.FromKey(Bytes.FromHexString("0x123A")).SliceTo(length);
        NibblePath pathB = NibblePath.FromKey(Bytes.FromHexString("0x123B")).SliceTo(length);

        Span<byte> writtenA = pathA.WriteTo(stackalloc byte[pathA.MaxByteLength]);
        Span<byte> writtenB = pathB.WriteTo(stackalloc byte[pathB.MaxByteLength]);

        Assert.That(writtenA.ToArray(), Is.EqualTo(writtenB.ToArray()));
    }

    [TestCase(0, 4)]
    [TestCase(0, 3)]
    [TestCase(1, 3)]
    [TestCase(1, 2)]
    public void AppendNibble(int from, int length)
    {
        const byte first = 0xDC;
        const byte second = 0xBA;
        const byte nibble = 0xF;

        Span<byte> span = [first, second];

        NibblePath path = NibblePath.FromKey(span, from).SliceTo(length);
        NibblePath appended = path.AppendNibble(nibble, stackalloc byte[path.MaxByteLength + 1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(appended.Length, Is.EqualTo(length + 1));
            Assert.That(appended.FindFirstDifferentNibble(path), Is.EqualTo(length));
            Assert.That(appended.GetAt(length), Is.EqualTo(nibble));
        }
    }

    [Test]
    public void Append_nibble_odd()
    {
        const byte first = 0xDC;
        const byte second = 0xBA;
        const byte nibble = 0xE;

        Span<byte> span = [first, second];
        NibblePath path = NibblePath.FromKey(span, 1);

        NibblePath firstNibblePath = NibblePath.FromKey([nibble << NibblePath.NibbleShift]).SliceTo(1);

        NibblePath appended = firstNibblePath.Append(path, stackalloc byte[NibblePath.FullKeccakByteLength]);
        Span<byte> expected = [(nibble << NibblePath.NibbleShift) | (first & 0x0F), second];

        Assert.That(NibblePath.FromKey(expected).Equals(appended), Is.True);
    }

    [Test]
    public void Append_nibble_even()
    {
        const byte first = 0xDC;
        const byte nibble = 0xE;

        Span<byte> span = [first];
        NibblePath path = NibblePath.FromKey(span, 1);

        NibblePath firstNibblePath = NibblePath.FromKey([nibble << NibblePath.NibbleShift]).SliceTo(1);

        NibblePath appended = firstNibblePath.Append(path, stackalloc byte[NibblePath.FullKeccakByteLength]);
        // Upstream expected "EDC" and discarded the comparison; "E" appended with "C" is "EC".
        Span<byte> expected = [(nibble << NibblePath.NibbleShift) | (first & 0x0F)];

        Assert.That(NibblePath.FromKey(expected).Equals(appended), Is.True);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Append_byte_aligned(bool oddEnd)
    {
        const int oddity = 1;

        NibblePath first = NibblePath.Single(0xA, oddity);
        byte[] bytes = Bytes.FromHexString("0x12345678");

        int length = oddEnd ? bytes.Length - 1 : bytes.Length;
        NibblePath second = NibblePath.FromKey(bytes, 0, length);

        NibblePath appended = first.Append(second, stackalloc byte[NibblePath.FullKeccakByteLength]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(appended.Odd, Is.EqualTo(oddity));
            Assert.That(appended.SliceTo(1).Equals(first), Is.True);
            Assert.That(appended.SliceFrom(1).Equals(second), Is.True);
            Assert.That(appended.Length, Is.EqualTo(1 + length));
        }
    }

    [TestCaseSource(nameof(GetRawNibbles))]
    public void Raw_nibbles(byte[] nibbles)
    {
        NibblePath path = NibblePath.FromRawNibbles(nibbles, stackalloc byte[(nibbles.Length + 1) / 2]);

        Assert.That(path.Length, Is.EqualTo(nibbles.Length));
        for (int i = 0; i < nibbles.Length; i++)
        {
            Assert.That(path[i], Is.EqualTo(nibbles[i]));
        }
    }

    public static IEnumerable<TestCaseData> GetRawNibbles()
    {
        yield return new TestCaseData(new byte[] { 1, 2, 3 }).SetName("Short - odd");
        yield return new TestCaseData(new byte[] { 1, 2, 3, 4 }).SetName("Short - even");

        byte[] @long =
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 0xA, 0xB, 0xC, 0xD, 0xE,
            1, 2, 3, 4, 5, 6, 7, 8, 9, 0xA, 0xB, 0xC, 0xD, 0xE,
            1, 2, 3, 4, 5, 6, 7, 8, 9, 0xA, 0xB, 0xC, 0xD, 0xE,
            1, 2, 3, 4, 5, 6, 7, 8, 9, 0xA, 0xB, 0xC, 0xD, 0xE,
        ];

        yield return new TestCaseData(@long[..^1]).SetName("Long - odd");
        yield return new TestCaseData(@long).SetName("Long - even");
    }

    [Parallelizable(ParallelScope.None)]
    [TestCase(0, 0, TestName = "Empty")]
    [TestCase(0, 1, TestName = "Single, start from 0")]
    [TestCase(1, 1, TestName = "Single, start from 1")]
    [TestCase(1, 4, TestName = "Odd 1")]
    [TestCase(1, 7, TestName = "Odd 2")]
    [TestCase(1, 9, TestName = "Odd 3")]
    [TestCase(1, 11, TestName = "Odd 4")]
    [TestCase(1, 19, TestName = "Odd 5")]
    [TestCase(0, 4, TestName = "Even 1")]
    [TestCase(0, 7, TestName = "Even 2")]
    [TestCase(0, 9, TestName = "Even 3")]
    [TestCase(0, 11, TestName = "Even 4")]
    [TestCase(0, 20, TestName = "Even 5")]
    public void GetHashCode(int from, int length)
    {
        NibblePath path = NibblePath.FromKey(Bytes.FromHexString("0x123456789ABCDEF07364")).SliceFrom(from).SliceTo(length);
        int hash = path.GetHashCode();

        Assert.That(_hashes.Add(hash), Is.True);
    }

    private readonly HashSet<int> _hashes = [];

    [Test]
    public void Double()
    {
        const int maxNibble = 16;

        for (byte nibble0 = 0; nibble0 < maxNibble; nibble0++)
        {
            for (byte nibble1 = 0; nibble1 < maxNibble; nibble1++)
            {
                NibblePath path = NibblePath.DoubleEven(nibble0, nibble1);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(path.IsOdd, Is.False);
                    Assert.That(path.Length, Is.EqualTo(2));
                    Assert.That(path.Nibble0, Is.EqualTo(nibble0));
                    Assert.That(path.GetAt(0), Is.EqualTo(nibble0));
                    Assert.That(path.GetAt(1), Is.EqualTo(nibble1));
                }
            }
        }
    }

    [Test]
    public void Builder()
    {
        const int count = 2;

        NibblePath.Builder builder = new(stackalloc byte[count]);

        builder.Push(1);
        Assert.That(builder.Current.Equals(NibblePath.Single(1, 0)), Is.True);
        builder.Pop();

        builder.Push(2);
        Assert.That(builder.Current.Equals(NibblePath.Single(2, 0)), Is.True);
        builder.Pop();

        builder.Push(3, 4);
        Assert.That(builder.Current.Equals(NibblePath.DoubleEven(3, 4)), Is.True);
        builder.Pop();
        builder.Pop();

        builder.Push(5);
        Assert.That(builder.Append(NibblePath.Single(6, 1)).Equals(NibblePath.DoubleEven(5, 6)), Is.True);
        builder.Pop();
    }
}
