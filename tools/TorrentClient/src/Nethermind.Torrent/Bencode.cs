// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Globalization;
using System.Text;

namespace Nethermind.Torrent;

internal abstract class BValue
{
}

internal sealed class BInteger(long value) : BValue
{
    public long Value { get; } = value;
}

internal sealed class BString(byte[] bytes) : BValue
{
    public byte[] Bytes { get; } = bytes;

    public string Text => Encoding.UTF8.GetString(Bytes);
}

internal sealed class BList(List<BValue> values) : BValue
{
    public List<BValue> Values { get; } = values;
}

internal sealed class BDictionary(Dictionary<string, BValue> values) : BValue
{
    public Dictionary<string, BValue> Values { get; } = values;

    public BValue this[string key] => Values[key];

    public bool TryGetValue(string key, out BValue? value) => Values.TryGetValue(key, out value);
}

internal sealed class BencodeDocument(BValue root, byte[]? infoBytes)
{
    public BValue Root { get; } = root;

    public byte[]? InfoBytes { get; } = infoBytes;

    public static BencodeDocument Decode(ReadOnlySpan<byte> data)
    {
        BencodeParser parser = new(data);
        BValue value = parser.ParseValue(0);
        if (parser.Position != data.Length)
        {
            throw new FormatException("Unexpected trailing bencode data.");
        }

        return new BencodeDocument(value, parser.InfoBytes);
    }
}

internal ref struct BencodeParser(ReadOnlySpan<byte> data)
{
    private const int MaxDepth = 128;
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; private set; }

    public byte[]? InfoBytes { get; private set; }

    public BValue ParseValue(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException($"Bencode nesting exceeds the maximum depth of {MaxDepth}.");
        }

        EnsureAvailable(1);
        byte marker = _data[Position];
        if (marker == (byte)'i')
        {
            return ParseInteger();
        }

        if (marker == (byte)'l')
        {
            return ParseList(depth);
        }

        if (marker == (byte)'d')
        {
            return ParseDictionary(depth);
        }

        if (marker >= (byte)'0' && marker <= (byte)'9')
        {
            return ParseString();
        }

        throw new FormatException($"Unexpected bencode marker '{(char)marker}' at offset {Position}.");
    }

    private BInteger ParseInteger()
    {
        Position++;
        int start = Position;
        while (Position < _data.Length && _data[Position] != (byte)'e')
        {
            Position++;
        }

        EnsureAvailable(1);
        ReadOnlySpan<byte> numberBytes = _data[start..Position];
        Position++;
        string numberText = Encoding.ASCII.GetString(numberBytes);
        if (!long.TryParse(numberText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
        {
            throw new FormatException($"Invalid bencode integer '{numberText}'.");
        }

        return new BInteger(value);
    }

    private BString ParseString()
    {
        int length = 0;
        bool hasDigit = false;
        while (Position < _data.Length && _data[Position] != (byte)':')
        {
            byte digit = _data[Position];
            if (digit < (byte)'0' || digit > (byte)'9')
            {
                throw new FormatException($"Invalid bencode string length at offset {Position}.");
            }

            checked
            {
                length = length * 10 + digit - (byte)'0';
            }

            hasDigit = true;
            Position++;
        }

        EnsureAvailable(1);
        if (!hasDigit)
        {
            throw new FormatException("Bencode string length is empty.");
        }

        Position++;
        if (_data.Length - Position < length)
        {
            throw new FormatException("Bencode string exceeds available data.");
        }

        byte[] bytes = _data.Slice(Position, length).ToArray();
        Position += length;
        return new BString(bytes);
    }

    private BList ParseList(int depth)
    {
        Position++;
        List<BValue> values = [];
        while (true)
        {
            EnsureAvailable(1);
            if (_data[Position] == (byte)'e')
            {
                Position++;
                return new BList(values);
            }

            values.Add(ParseValue(depth + 1));
        }
    }

    private BDictionary ParseDictionary(int depth)
    {
        Position++;
        Dictionary<string, BValue> values = new(StringComparer.Ordinal);
        while (true)
        {
            EnsureAvailable(1);
            if (_data[Position] == (byte)'e')
            {
                Position++;
                return new BDictionary(values);
            }

            BString key = ParseString();
            string keyText = Encoding.UTF8.GetString(key.Bytes);
            int valueStart = Position;
            BValue value = ParseValue(depth + 1);
            if (depth == 0 && keyText == "info")
            {
                InfoBytes = _data[valueStart..Position].ToArray();
            }

            values[keyText] = value;
        }
    }

    private void EnsureAvailable(int length)
    {
        if (_data.Length - Position < length)
        {
            throw new FormatException("Unexpected end of bencode data.");
        }
    }
}

internal static class Bencode
{
    public static byte[] Encode(BValue value)
    {
        ArrayBufferWriter<byte> writer = new();
        Write(value, writer);
        return writer.WrittenSpan.ToArray();
    }

    public static void Write(BValue value, IBufferWriter<byte> writer)
    {
        switch (value)
        {
            case BInteger integer:
                WriteAscii(writer, "i");
                WriteAscii(writer, integer.Value.ToString(CultureInfo.InvariantCulture));
                WriteAscii(writer, "e");
                break;
            case BString text:
                WriteAscii(writer, text.Bytes.Length.ToString(CultureInfo.InvariantCulture));
                WriteAscii(writer, ":");
                writer.Write(text.Bytes);
                break;
            case BList list:
                WriteAscii(writer, "l");
                for (int i = 0; i < list.Values.Count; i++)
                {
                    Write(list.Values[i], writer);
                }

                WriteAscii(writer, "e");
                break;
            case BDictionary dictionary:
                WriteAscii(writer, "d");
                List<string> keys = [.. dictionary.Values.Keys];
                keys.Sort(StringComparer.Ordinal);
                for (int i = 0; i < keys.Count; i++)
                {
                    string key = keys[i];
                    byte[] keyBytes = Encoding.UTF8.GetBytes(key);
                    Write(new BString(keyBytes), writer);
                    Write(dictionary.Values[key], writer);
                }

                WriteAscii(writer, "e");
                break;
            default:
                throw new InvalidOperationException($"Unsupported bencode value {value.GetType().Name}.");
        }
    }

    public static BDictionary Dictionary(params KeyValuePair<string, BValue>[] values)
    {
        Dictionary<string, BValue> dictionary = new(StringComparer.Ordinal);
        for (int i = 0; i < values.Length; i++)
        {
            dictionary[values[i].Key] = values[i].Value;
        }

        return new BDictionary(dictionary);
    }

    public static BString String(string value) => new(Encoding.UTF8.GetBytes(value));

    public static BString Bytes(ReadOnlySpan<byte> value) => new(value.ToArray());

    public static BInteger Integer(long value) => new(value);

    private static void WriteAscii(IBufferWriter<byte> writer, string value)
    {
        int byteCount = Encoding.ASCII.GetByteCount(value);
        Span<byte> span = writer.GetSpan(byteCount);
        int written = Encoding.ASCII.GetBytes(value, span);
        writer.Advance(written);
    }
}

internal static class BencodeAccessors
{
    public static BDictionary AsDictionary(this BValue value, string name)
        => value as BDictionary ?? throw new FormatException($"{name} must be a dictionary.");

    public static BList AsList(this BValue value, string name)
        => value as BList ?? throw new FormatException($"{name} must be a list.");

    public static long AsInteger(this BValue value, string name)
        => value is BInteger integer ? integer.Value : throw new FormatException($"{name} must be an integer.");

    public static byte[] AsBytes(this BValue value, string name)
        => value is BString text ? text.Bytes : throw new FormatException($"{name} must be a string.");

    public static string AsText(this BValue value, string name)
        => value is BString text ? text.Text : throw new FormatException($"{name} must be a string.");

    public static bool TryGetDictionary(this BDictionary dictionary, string key, out BDictionary? value)
    {
        if (dictionary.TryGetValue(key, out BValue? rawValue) && rawValue is BDictionary rawDictionary)
        {
            value = rawDictionary;
            return true;
        }

        value = null;
        return false;
    }
}
