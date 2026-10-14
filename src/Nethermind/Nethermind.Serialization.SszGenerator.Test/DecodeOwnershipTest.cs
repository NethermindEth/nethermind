// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;
using SszLib = Nethermind.Serialization.Ssz.Ssz;

namespace Nethermind.Serialization.SszGenerator.Test;

[NonParallelizable]
public class DecodeOwnershipTest
{
    [Test]
    public void Failed_setters_release_acquired_values_once([Values] bool retainBeforeThrow, [Values] bool collection)
    {
        ThrowingOwnedSetter.RetainBeforeThrow = retainBeforeThrow;
        ThrowingCollectionSetter.RetainBeforeThrow = retainBeforeThrow;
        byte[] data = [17, 0, 0, 0, 0, 0, 0, 0, 1];
        if (collection)
            Assert.That(() => ThrowingCollectionSetter.Decode(data.AsSpan(0, 8), out ThrowingCollectionSetter _), Throws.InstanceOf<InvalidDataException>());
        else
            Assert.That(() => ThrowingOwnedSetter.Decode(data, out ThrowingOwnedSetter _), Throws.InstanceOf<InvalidDataException>());
        AssertReleased(1);
    }

    public enum Layout { Fixed, Variable, Generic }

    [SetUp]
    public void SetUp() => SszOwnedUlongCollection.ResetCounts();

    [Test]
    public void Decode_releases_owned_fields_when_a_later_field_is_invalid([Values] bool nested)
    {
        byte[] data = new byte[nested ? 10 : 9];
        BinaryPrimitives.WriteUInt64LittleEndian(data, 17);
        data[8] = 1;
        data[^1] = 2;

        if (nested)
            Assert.Throws<InvalidDataException>(() => OwnedDecodeParent.Decode(data, out OwnedDecodeParent _));
        else
            Assert.Throws<InvalidDataException>(() => OwnedDecodeValue.Decode(data, out OwnedDecodeValue _));

        AssertReleased(1);
    }

    [Test]
    public void Decode_releases_a_generic_child_when_a_later_field_is_invalid()
    {
        byte[] data = CreateCollectionPayload(Layout.Generic, 1, valid: false)[4..];

        Assert.Throws<InvalidDataException>(() => GenericOwnedDecodeValue<OwnedDecodeValue>.Decode(data, out GenericOwnedDecodeValue<OwnedDecodeValue> _));

        AssertReleased(1);
    }

    [Test]
    public void Decode_releases_completed_collection_items_when_a_later_item_is_invalid([Values] Layout layout)
    {
        byte[] data = CreateCollectionPayload(layout, 3, valid: false);

        switch (layout)
        {
            case Layout.Fixed:
                Assert.Throws<InvalidDataException>(() => OwnedDecodeValue.Decode(data, out OwnedDecodeValue[] _));
                break;
            case Layout.Variable:
                Assert.Throws<InvalidDataException>(() => VariableOwnedDecodeValue.Decode(data, out VariableOwnedDecodeValue[] _));
                break;
            case Layout.Generic:
                Assert.Throws<InvalidDataException>(() => GenericOwnedDecodeValue<OwnedDecodeValue>.Decode(data, out GenericOwnedDecodeValue<OwnedDecodeValue>[] _));
                break;
        }

        AssertReleased(3);
    }

    [Test]
    public void Decode_transfers_successful_collection_items_to_the_caller([Values] Layout layout, [Values(1, 3)] int count)
    {
        byte[] data = CreateCollectionPayload(layout, count, valid: true);

        switch (layout)
        {
            case Layout.Fixed:
                AssertDecodedItems<OwnedDecodeValue>(data, count, static item => item.Items);
                break;
            case Layout.Variable:
                AssertDecodedItems<VariableOwnedDecodeValue>(data, count, static item => item.Items);
                break;
            case Layout.Generic:
                AssertDecodedItems<GenericOwnedDecodeValue<OwnedDecodeValue>>(data, count, static item => item.Child.Items);
                break;
        }
    }

    private static void AssertDecodedItems<T>(byte[] data, int count, Func<T, SszOwnedUlongCollection> getItems) where T : ISszCodec<T>
    {
        T.Decode(data, out T[] decoded);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded, Has.Length.EqualTo(count));
                Assert.That(SszOwnedUlongCollection.CreatedCount, Is.EqualTo(count));
                Assert.That(SszOwnedUlongCollection.DisposedCount, Is.Zero);
                for (int index = 0; index < decoded.Length; index++)
                    Assert.That(getItems(decoded[index]).AsSpan().ToArray(), Is.EqualTo(new ulong[] { (ulong)(index + 1) * 17 }));
            }
        }
        finally
        {
            foreach (T item in decoded)
                SszLib.ReleaseDecoded(item);
        }

        AssertReleased(count);
    }

    private static void AssertReleased(int count)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SszOwnedUlongCollection.CreatedCount, Is.EqualTo(count));
            Assert.That(SszOwnedUlongCollection.DisposedCount, Is.EqualTo(count));
        }
    }

    private static byte[] CreateCollectionPayload(Layout layout, int count, bool valid)
    {
        int itemLength = layout switch { Layout.Fixed => 9, Layout.Variable => 13, _ => 18 };
        int fixedLength = layout == Layout.Fixed ? 0 : count * sizeof(int);
        byte[] data = new byte[fixedLength + count * itemLength];
        for (int index = 0; index < count; index++)
        {
            int offset = fixedLength + index * itemLength;
            if (layout != Layout.Fixed)
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(index * sizeof(int)), offset);

            if (layout == Layout.Variable)
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + sizeof(ulong)), 12);

            int valueOffset = offset;
            if (layout == Layout.Generic)
            {
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), 8);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + sizeof(int)), 17);
                valueOffset += 8;
                data[valueOffset + sizeof(ulong)] = 1;
            }

            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(valueOffset), (ulong)(index + 1) * 17);
            data[offset + itemLength - 1] = !valid && index == count - 1 ? (byte)2 : (byte)1;
        }

        return data;
    }
}

[SszContainer]
public partial struct ThrowingOwnedSetter
{
    private OwnedDecodeValue _child;
    [SszIgnore]
    public static bool RetainBeforeThrow { get; set; }
    public OwnedDecodeValue Child
    {
        get => _child;
        set
        {
            if (RetainBeforeThrow) _child = value;
            throw new InvalidDataException("Setter rejected the decoded child.");
        }
    }
}

[SszContainer]
public partial struct ThrowingCollectionSetter
{
    private SszOwnedUlongCollection _items;
    [SszIgnore]
    public static bool RetainBeforeThrow { get; set; }
    [SszVector(1)]
    public SszOwnedUlongCollection Items
    {
        get => _items;
        set
        {
            if (RetainBeforeThrow) _items = value;
            throw new InvalidDataException("Setter rejected the decoded collection.");
        }
    }
}

[SszContainer]
public partial struct OwnedDecodeValue
{
    [SszVector(1)]
    public SszOwnedUlongCollection Items { get; set; }

    public bool Flag { get; set; }
}

[SszContainer]
public partial struct OwnedDecodeParent
{
    public OwnedDecodeValue Child { get; set; }

    public bool Flag { get; set; }
}

[SszContainer]
public partial struct VariableOwnedDecodeValue
{
    [SszVector(1)]
    public SszOwnedUlongCollection Items { get; set; }

    [SszList(2)]
    public bool[] Flags { get; set; }
}

[SszContainer]
public partial struct GenericOwnedDecodeValue<T> where T : struct, ISszCodec<T>
{
    public T Child { get; set; }

    [SszList(2)]
    public bool[] Flags { get; set; }
}
