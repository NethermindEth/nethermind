// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NonBlocking;

namespace Nethermind.Network.Discovery.Portal;

public interface IUnion {}

public class SelectorAttribute(byte selectorByte) : System.Attribute
{
    public byte SelectorByte = selectorByte;
}

public class SlowSSZ
{
    private const int BYTES_PER_LENGTH_OFFSET = 4;

    // TODO: Obviously, returning a byte array on every item is not very efficient. need some kind of streaming writes,
    // but it gets complicated with container. One idea is to do the same thing like RLP where we get the length ahead
    // of time before serializing. This can be further optimized by putting the lengths in a stack and popping them
    // one by one as it is being used.
    public static byte[] Serialize(object obj)
    {
        if (obj is int asInt)
        {
            byte[] buffer = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, asInt);
            return buffer;
        }

        if (obj is bool asBool)
        {
            return [(byte)(asBool ? 1 : 0)];
        }

        if (obj is byte asbyte)
        {
            return [asbyte];
        }

        if (obj is ushort asshort)
        {
            byte[] buffer = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, asshort);
            return buffer;
        }

        if (obj is ulong asLong)
        {
            byte[] buffer = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, asLong);
            return buffer;
        }

        if (obj is UInt256 asUint256)
        {
            return asUint256.ToLittleEndian();
        }

        if (obj is byte[] asBytes)
        {
            return asBytes;
        }

        if (obj is IUnion)
        {
            return SerializeUnion(obj);
        }

        return SerializeContainer(obj);
    }

    private static byte[] SerializeUnion(object o)
    {
        var targetType = o.GetType();
        var properties = targetType.GetProperties();
        for (var i = 0; i < properties.Length; i++)
        {
            var field = properties[i];
            var selectorAttr = field.GetCustomAttribute<SelectorAttribute>();
            if (selectorAttr != null)
            {
                var fieldVal = field.GetValue(o);
                if (fieldVal != null)
                {
                    byte[] serialized = Serialize(fieldVal);
                    return Bytes.Concat(new[] { selectorAttr.SelectorByte }, serialized);
                }
            }
        }

        throw new InvalidOperationException("Attempting to serialize a union but no option is non null");
    }

    private static byte[] SerializeContainer(object o)
    {
        var targetType = o.GetType();
        if (targetType.IsArray)
        {
            return SerializeArray((Array)o, targetType.GetElementType()!);
        }

        List<(bool isFixed, byte[] serializedForm)> serializedMembers =
            new List<(bool isFixed, byte[] serializedForm)>();

        int fixedPartLength = 0;

        var properties = targetType.GetProperties();
        for (var i = 0; i < properties.Length; i++)
        {
            var field = properties[i];
            var obj = field.GetValue(o);
            if (IsFixed(field.PropertyType))
            {
                var serialized = Serialize(obj!);
                serializedMembers.Add((true, serialized));
                fixedPartLength += serialized.Length;
            }
            else
            {

                var serialized = Serialize(obj!);
                serializedMembers.Add((false, serialized));
                fixedPartLength += BYTES_PER_LENGTH_OFFSET;
            }
        }

        return CombineSerializedMembers(fixedPartLength, serializedMembers);
    }

    private static byte[] CombineSerializedMembers(int fixedPartLength, List<(bool isFixed, byte[] serializedForm)> serializedMembers)
    {
        byte[] final = Array.Empty<byte>();
        int currentDynamicOffset = fixedPartLength;

        foreach (var serializedMember in serializedMembers)
        {
            if (serializedMember.isFixed)
            {
                final = Bytes.Concat(final, serializedMember.serializedForm);
            }
            else
            {
                final = Bytes.Concat(final, Serialize((int)currentDynamicOffset));
                currentDynamicOffset += serializedMember.serializedForm.Length;
            }
        }

        foreach (var serializedMember in serializedMembers)
        {
            if (!serializedMember.isFixed)
            {
                final = Bytes.Concat(final, serializedMember.serializedForm);
            }
        }

        return final;
    }

    private static byte[] SerializeArray(Array asArray, Type elementType)
    {
        bool isFixed = IsFixed(elementType);

        List<(bool isFixed, byte[] serializedForm)> serializedMembers =
            new List<(bool isFixed, byte[] serializedForm)>();

        int fixedPartLength = 0;

        foreach (var obj in asArray)
        {
            if (isFixed)
            {
                var serialized = Serialize(obj!);
                serializedMembers.Add((true, serialized));
                fixedPartLength += serialized.Length;
            }
            else
            {

                var serialized = Serialize(obj!);
                serializedMembers.Add((false, serialized));
                fixedPartLength += BYTES_PER_LENGTH_OFFSET;
            }
        }

        return CombineSerializedMembers(fixedPartLength, serializedMembers);
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> span)
    {
        return (T)Deserialize(span, typeof(T));
    }

    public static object Deserialize(ReadOnlySpan<byte> span, Type targetType)
    {
        if (targetType == typeof(int))
        {
            return BinaryPrimitives.ReadInt32LittleEndian(span[..4]);
        }

        if (targetType == typeof(bool))
        {
            return span[0] != 0;
        }

        if (targetType == typeof(byte))
        {
            return span[0];
        }

        if (targetType == typeof(ushort))
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(span[..2]);
        }

        if (targetType == typeof(ulong))
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(span[..8]);
        }

        if (targetType == typeof(UInt256))
        {
            return new UInt256(span[..32]);
        }

        if (targetType == typeof(byte[]))
        {
            return span.ToArray();
        }

        if (targetType.IsAssignableTo(typeof(IUnion)))
        {
            return DeserializeUnion(span, targetType);
        }

        return DeserializeContainer(span, targetType);
    }

    // TODO: Special null handling.
    private static object DeserializeUnion(ReadOnlySpan<byte> span, Type targetType)
    {
        byte selector = span[0];
        ReadOnlySpan<byte> item = span[1..];

        object target = Activator.CreateInstance(targetType)!;

        var properties = targetType.GetProperties();
        for (var i = 0; i < properties.Length; i++)
        {
            var field = properties[i];
            var selectorAttr = field.GetCustomAttribute<SelectorAttribute>();
            if (selectorAttr != null && selectorAttr.SelectorByte == selector)
            {
                var propType = field.PropertyType;
                object result;

                Type? underlyingType = Nullable.GetUnderlyingType(propType);
                if (underlyingType != null)
                {
                    result = Deserialize(item, underlyingType);
                }
                else
                {
                    result = Deserialize(item, field.PropertyType);
                }

                field.SetValue(target, result);
                return target;
            }
        }

        throw new InvalidOperationException($"Got selector {selector} while no field specify it");
    }

    // Note, for non fixed item, the length of the span must be the whole item and nothing more.
    private static object DeserializeContainer(ReadOnlySpan<byte> span, Type targetType)
    {
        if (targetType.IsArray)
        {
            return DeserializeArray(span, targetType.GetElementType()!);
        }

        int currentIdx = 0;

        object target = Activator.CreateInstance(targetType)!;
        var properties = targetType.GetProperties();

        List<(int fieldIdx, int offset)> dynamicFieldOffsets = new List<(int fieldIdx, int offset)>();

        for (var i = 0; i < properties.Length; i++)
        {
            var field = properties[i];
            int fixedSize = GetFixedSize(field.PropertyType);
            if (fixedSize != -1)
            {
                object result = Deserialize(span[currentIdx..], field.PropertyType);
                field.SetValue(target, result);
                currentIdx += fixedSize;
            }
            else
            {
                int offset = Deserialize<int>(span[currentIdx..]);
                currentIdx += BYTES_PER_LENGTH_OFFSET; // Fixed offset length

                dynamicFieldOffsets.Add((i, offset));

                object result = Deserialize(span[offset..], field.PropertyType);
                field.SetValue(target, result);
            }
        }

        for (var i = 0; i < dynamicFieldOffsets.Count; i++)
        {
            (int fieldIdx, int offset) = dynamicFieldOffsets[i];

            // length of a non fixed element need to be inferred accurately.
            int length = 0;
            if (i != dynamicFieldOffsets.Count - 1)
            {
                length = dynamicFieldOffsets[i + 1].offset - offset;
            }
            else
            {
                length = span.Length - offset;
            }


            var field = properties[fieldIdx];
            object result = Deserialize(span[offset..(offset + length)], field.PropertyType);
            field.SetValue(target, result);
        }

        return target;
    }

    private static object DeserializeArray(ReadOnlySpan<byte> span, Type elementType)
    {
        int numElement = 0;
        int perItemLength = GetFixedSize(elementType);
        if (perItemLength != -1)
        {
            numElement = span.Length / perItemLength;

            Array resultArray = Array.CreateInstance(elementType, numElement);
            for (int i = 0; i < numElement; i++)
            {
                object result = Deserialize(span[(i*perItemLength)..], elementType);
                resultArray.SetValue(result, i);
            }

            return resultArray;
        }
        else
        {
            if (span.Length == 0)
            {
                return Array.CreateInstance(elementType, 0);
            }

            // To get the size of the array, we get the offset of the first item,
            // which is also the size of the fixed portion.
            // Since the fixed portion should only be all length, we divide it by 4.
            int firstOffset = Deserialize<int>(span);
            numElement = firstOffset / BYTES_PER_LENGTH_OFFSET;

            // TODO: This can be done so much more efficiently
            int[] offsets = new int[numElement];
            for (int i = 0; i < numElement; i++)
            {
                offsets[i] = Deserialize<int>(span[(i*BYTES_PER_LENGTH_OFFSET)..]);
            }

            Array resultArray = Array.CreateInstance(elementType, numElement);
            for (int i = 0; i < numElement; i++)
            {
                int offset = offsets[i];

                // length of a non fixed element need to be inferred accurately.
                int length = 0;
                if (i != numElement - 1)
                {
                    length = offsets[i + 1] - offsets[i];
                }
                else
                {
                    length = span.Length - offsets[i];
                }

                object result = Deserialize(span[offset..(offset + length)], elementType);
                resultArray.SetValue(result, i);
            }

            return resultArray;
        }
    }

    private static ConcurrentDictionary<Type, int> _fixedSizeCache = new ConcurrentDictionary<Type, int>();

    private static int GetFixedSize(Type targetType)
    {
        if (targetType == typeof(int))
        {
            return 4;
        }

        if (targetType == typeof(byte))
        {
            return 1;
        }

        if (targetType == typeof(ushort))
        {
            return 2;
        }

        if (targetType == typeof(ulong))
        {
            return 8;
        }

        if (targetType == typeof(UInt256))
        {
            return 32;
        }

        if (targetType.IsArray)
        {
            return -1; // Well... I guess we can have some sort of attribute to specify its fixed size.
        }

        if (_fixedSizeCache.TryGetValue(targetType, out int size))
        {
            return size;
        }

        var properties = targetType.GetProperties();
        size = 0;
        for (var i = 0; i < properties.Length; i++)
        {
            var field = properties[i];
            int curItemSize = GetFixedSize(field.PropertyType);
            if (curItemSize == -1) return -1;

            size += curItemSize;
        }

        _fixedSizeCache[targetType] = size;
        return size;
    }

    private static bool IsFixed(Type targetType)
    {
        return GetFixedSize(targetType) != -1;
    }
}
