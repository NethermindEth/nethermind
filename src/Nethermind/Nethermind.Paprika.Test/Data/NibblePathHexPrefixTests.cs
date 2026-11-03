// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Paprika.Data;

namespace Paprika.Tests.Data;

[TestFixture(true)]
[TestFixture(false)]
public class NibblePathHexPrefixTests(bool odd)
{
    private readonly bool _odd = odd;
    private readonly int _shift = odd ? 0 : NibblePath.NibbleShift;

    [TestCase(false, (byte)3, (byte)19)]
    [TestCase(true, (byte)3, (byte)51)]
    public void Encode_gives_correct_output_when_one(bool flag, byte nibble1, byte byte1)
    {
        Span<byte> span = stackalloc byte[1];
        span[0] = (byte)(nibble1 << _shift);

        NibblePath path = _odd ? NibblePath.FromKey(span, 1) : NibblePath.FromKey(span).SliceTo(1);

        Span<byte> destination = stackalloc byte[path.HexEncodedLength];

        path.HexEncode(destination, flag);

        Assert.That(destination.ToArray(), Is.EqualTo(new[] { byte1 }));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
    [TestCase(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
    public void Encode_gives_correct_output_when_odd(bool flag, byte nibble1, byte nibble2, byte nibble3,
        byte byte1, byte byte2)
    {
        NibblePath path;
        if (_odd)
        {
            byte[] key = [nibble1, (byte)((nibble2 << NibblePath.NibbleShift) | nibble3)];
            path = NibblePath.FromKey(key, 1);
        }
        else
        {
            byte[] key = [(byte)((nibble1 << NibblePath.NibbleShift) | nibble2), (byte)(nibble3 << NibblePath.NibbleShift)];
            path = NibblePath.FromKey(key).SliceTo(3);
        }

        Span<byte> destination = stackalloc byte[path.HexEncodedLength];
        path.HexEncode(destination, flag);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(path.HexEncodedLength, Is.EqualTo(2));
            Assert.That(destination.ToArray(), Is.EqualTo(new[] { byte1, byte2 }));
        }
    }

    [TestCase(false, (byte)3, (byte)7, (byte)0, (byte)55)]
    [TestCase(true, (byte)3, (byte)7, (byte)32, (byte)55)]
    public void Encode_gives_correct_output_when_even(bool flag, byte nibble1, byte nibble2, byte byte1, byte byte2)
    {
        NibblePath path;
        if (_odd)
        {
            byte[] key = [nibble1, (byte)(nibble2 << NibblePath.NibbleShift)];
            path = NibblePath.FromKey(key, 1).SliceTo(2);
        }
        else
        {
            byte[] key = [(byte)((nibble1 << NibblePath.NibbleShift) | nibble2)];
            path = NibblePath.FromKey(key);
        }

        Span<byte> destination = stackalloc byte[path.HexEncodedLength];

        path.HexEncode(destination, flag);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(path.HexEncodedLength, Is.EqualTo(2));
            Assert.That(destination.ToArray(), Is.EqualTo(new[] { byte1, byte2 }));
        }
    }
}
