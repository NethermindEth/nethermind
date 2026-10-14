// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;

namespace Nethermind.Serialization.SszGenerator.Test;

public class GenericLayoutTest
{
    [Test]
    public void Nongeneric_derived_codec_uses_the_closed_inherited_field_layout()
    {
        GenericLayoutDerived value = new() { Value = new() { Value = 0x0102030405060708 }, Marker = 0xab };
        byte[] expected = [8, 7, 6, 5, 4, 3, 2, 1, 0xab];
        GenericLayoutDerived.Decode(expected, out GenericLayoutDerived decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GenericLayoutDerived.GetLength(value), Is.EqualTo(expected.Length));
            Assert.That(GenericLayoutDerived.Encode(value), Is.EqualTo(expected));
            Assert.That(decoded.Value.Value, Is.EqualTo(value.Value.Value));
            Assert.That(decoded.Marker, Is.EqualTo(value.Marker));
        }
    }

    [Test]
    public void Closed_generic_members_preserve_their_declared_variable_layout()
    {
        const ulong value = 0x0102030405060708;
        GenericLayoutParent parent = new()
        {
            Child = new() { Value = new() { Value = value } },
            Marker = 0xab
        };
        byte[] expected = [5, 0, 0, 0, 0xab, 4, 0, 0, 0, 8, 7, 6, 5, 4, 3, 2, 1];
        byte[] destination = new byte[expected.Length];
        Array.Fill(destination, (byte)0xcc);
        GenericLayoutParent.Encode(destination, parent);
        GenericLayoutParent.Decode(expected, out GenericLayoutParent decoded);
        GenericLayoutParent.Merkleize(parent, out UInt256 root);
        byte[] chunks = new byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(chunks, value);
        chunks[32] = parent.Marker;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GenericLayoutParent.GetLength(parent), Is.EqualTo(expected.Length));
            Assert.That(GenericLayoutEnvelope<GenericLayoutLeaf>.Encode(parent.Child), Is.EqualTo(expected[5..]));
            Assert.That(GenericLayoutParent.Encode(parent), Is.EqualTo(expected));
            Assert.That(destination, Is.EqualTo(expected));
            Assert.That(decoded.Child.Value.Value, Is.EqualTo(value));
            Assert.That(decoded.Marker, Is.EqualTo(parent.Marker));
            Assert.That(root, Is.EqualTo(new UInt256(SHA256.HashData(chunks))));
        }
    }
}

[SszContainer]
public partial struct GenericLayoutLeaf
{
    public ulong Value { get; set; }
}

[SszContainer]
public partial struct GenericLayoutEnvelope<T> where T : struct, ISszCodec<T>
{
    public T Value { get; set; }
}

[SszContainer]
public partial struct GenericLayoutParent
{
    public GenericLayoutEnvelope<GenericLayoutLeaf> Child { get; set; }

    public byte Marker { get; set; }
}

public class GenericLayoutBase<T>
{
    public T Value { get; set; } = default!;
}

[SszContainer]
public partial class GenericLayoutDerived : GenericLayoutBase<GenericLayoutLeaf>
{
    public byte Marker { get; set; }
}
