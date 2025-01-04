// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie;

public readonly struct TrieKey : IEquatable<TrieKey>
{
    private readonly static byte[][] _singleBytes = [[0], [1], [2], [3], [4], [5], [6], [7], [8], [9], [10], [11], [12], [13], [14], [15]];
    private readonly static TrieKey[] _singleByteKeys = [new(_singleBytes[0]), new(_singleBytes[1]), new(_singleBytes[2]), new(_singleBytes[3]), new(_singleBytes[4]), new(_singleBytes[5]), new(_singleBytes[6]), new(_singleBytes[7]), new(_singleBytes[8]), new(_singleBytes[9]), new(_singleBytes[10]), new(_singleBytes[11]), new(_singleBytes[12]), new(_singleBytes[13]), new(_singleBytes[14]), new(_singleBytes[15])];
    public static TrieKey Empty { get; } = new(Array.Empty<byte>());

    private readonly byte[]? _keyPart0;
    private readonly byte[]? _keyPart1;

    public TrieKey(byte[] key) => _keyPart0 = key;
    public TrieKey(byte keyPart0, TrieKey keyPart1)
    {
        if (keyPart1.Length == 0)
        {
            _keyPart0 = _singleBytes[keyPart0];
        }
        else if (keyPart1._keyPart1 is null)
        {
            _keyPart0 = _singleBytes[keyPart0];
            _keyPart1 = keyPart1._keyPart0;
        }
        else
        {
            // Often slice off the first byte, so just combine keyPart1 parts
            _keyPart0 = _singleBytes[keyPart0];
            _keyPart1 = Bytes.Concat(keyPart1._keyPart0, keyPart1._keyPart1);
        }
    }

    public TrieKey(TrieKey keyPart0, TrieKey keyPart1)
    {
        if (keyPart0.Length == 0 && keyPart1.Length == 0)
        {
            // Both parts are empty, return Empty
            _keyPart0 = Array.Empty<byte>();
        }
        else if (keyPart0.Length == 0)
        {
            _keyPart0 = keyPart1._keyPart0;
            _keyPart1 = keyPart1._keyPart1;
        }
        else if (keyPart1.Length == 0)
        {
            _keyPart0 = keyPart0._keyPart0;
            _keyPart1 = keyPart0._keyPart1;
        }
        else if (keyPart0._keyPart1 is null && keyPart1._keyPart1 is null)
        {
            _keyPart0 = keyPart0._keyPart0;
            _keyPart1 = keyPart1._keyPart0;
        }
        else if (keyPart0._keyPart1 is null)
        {
            // Combine the keyPart1._keyPart0 with the shorter part of keyPart0._keyPart0 or keyPart1._keyPart1
            if (keyPart0._keyPart0.Length >= keyPart1._keyPart1.Length)
            {
                _keyPart0 = keyPart0._keyPart0;
                int combinedLength = keyPart1._keyPart0.Length + keyPart1._keyPart1.Length;
                _keyPart1 = new byte[combinedLength];
                Array.Copy(keyPart1._keyPart0, 0, _keyPart1, 0, keyPart1._keyPart0.Length);
                Array.Copy(keyPart1._keyPart1, 0, _keyPart1, keyPart1._keyPart0.Length, keyPart1._keyPart1.Length);
            }
            else
            {
                int combinedLength = keyPart0._keyPart0.Length + keyPart1._keyPart0.Length;
                _keyPart0 = new byte[combinedLength];
                Array.Copy(keyPart0._keyPart0, 0, _keyPart0, 0, keyPart0._keyPart0.Length);
                Array.Copy(keyPart1._keyPart0, 0, _keyPart0, keyPart0._keyPart0.Length, keyPart1._keyPart0.Length);
                _keyPart1 = keyPart1._keyPart1;
            }
        }
        else if (keyPart1._keyPart1 is null)
        {
            // Combine the keyPart0._keyPart1 with the shorter part of keyPart0._keyPart0 or keyPart1._keyPart0
            if (keyPart0._keyPart0.Length >= keyPart1._keyPart0.Length)
            {
                _keyPart0 = keyPart0._keyPart0;
                int combinedLength = keyPart0._keyPart1.Length + keyPart1._keyPart1.Length;
                _keyPart1 = new byte[combinedLength];
                Array.Copy(keyPart0._keyPart1, 0, _keyPart1, 0, keyPart0._keyPart1.Length);
                Array.Copy(keyPart1._keyPart0, 0, _keyPart1, keyPart0._keyPart1.Length, keyPart1._keyPart0.Length);
            }
            else
            {
                int combinedLength = keyPart0._keyPart0.Length + keyPart1._keyPart1.Length;
                _keyPart0 = new byte[combinedLength];
                Array.Copy(keyPart0._keyPart0, 0, _keyPart0, 0, keyPart0._keyPart0.Length);
                Array.Copy(keyPart1._keyPart1, 0, _keyPart0, keyPart0._keyPart0.Length, keyPart1._keyPart1.Length);
                _keyPart1 = keyPart1._keyPart0;
            }
        }
        else
        {
            _keyPart0 = new byte[keyPart0.Length];
            Array.Copy(keyPart0._keyPart0, 0, _keyPart0, 0, keyPart0._keyPart0.Length);
            Array.Copy(keyPart0._keyPart1, 0, _keyPart0, keyPart0._keyPart0.Length, keyPart0._keyPart1.Length);

            _keyPart1 = new byte[keyPart1.Length];
            Array.Copy(keyPart1._keyPart0, 0, _keyPart1, 0, keyPart1._keyPart0.Length);
            Array.Copy(keyPart1._keyPart1, 0, _keyPart1, keyPart1._keyPart0.Length, keyPart1._keyPart1.Length);
        }
    }

    public TrieKey(byte[] keyPart0, byte[] keyPart1)
    {
        _keyPart0 = keyPart0;
        _keyPart1 = keyPart1;
    }

    public static implicit operator TrieKey(byte key) => _singleByteKeys[key];
    public static implicit operator TrieKey(byte[] key) => new(key);

    public readonly byte this[int index]
    {
        get
        {
            if (index < 0 || index >= Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index out of range");
            }
            return index < (_keyPart0?.Length ?? 0) ? _keyPart0![index] : _keyPart1![index - _keyPart0!.Length];
        }
    }

    public readonly int CommonPrefixLength(TrieKey other)
    {
        int commonLength = 0;
        int minLength = Math.Min(Length, other.Length);
        while (commonLength < minLength && this[commonLength] == other[commonLength])
        {
            commonLength++;
        }
        return commonLength;
    }

    public readonly TrieKey Slice(int start) => Slice(start, Length - start);

    public readonly TrieKey Slice(int start, int length)
    {
        if (start < 0 || length < 0 || start + length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Invalid start or length parameters");
        }

        if (length == 0)
        {
            return Empty;
        }

        if (length == 1)
        {
            return _singleByteKeys[this[start]];
        }

        int part0Length = _keyPart0?.Length ?? 0;

        // If slice is entirely within first part
        if (_keyPart1 is null || start + length <= part0Length)
        {
            if (start == 0 && length == part0Length)
            {
                return _keyPart1 is null ? this : new TrieKey(_keyPart0!);
            }
            byte[] newArray = new byte[length];
            Array.Copy(_keyPart0!, start, newArray, 0, length);
            return new TrieKey(newArray);
        }

        // If slice starts in second part
        if (start >= part0Length)
        {
            if (start == part0Length && length == _keyPart1!.Length)
            {
                return new TrieKey(_keyPart1);
            }
            byte[] newArray = new byte[length];
            Array.Copy(_keyPart1!, start - part0Length, newArray, 0, length);
            return new TrieKey(newArray);
        }

        // Slice spans both parts
        int lengthInPart0 = part0Length - start;
        int lengthInPart1 = length - lengthInPart0;

        byte[] newKey = new byte[length];
        Array.Copy(_keyPart0, start, newKey, 0, lengthInPart0);
        Array.Copy(_keyPart1, 0, newKey, lengthInPart0, lengthInPart1);

        return new TrieKey(newKey);
    }

    public readonly byte[] ToArray()
    {
        var length = Length;
        if (length == 0) return Array.Empty<byte>();
        if (length == 1) return _singleBytes[this[0]];
        if (_keyPart0.Length == length) return _keyPart0;

        byte[] result = new byte[length];
        _keyPart0?.CopyTo(result, 0);
        _keyPart1?.CopyTo(result, _keyPart0?.Length ?? 0);
        return result;
    }

    public readonly int Length => (_keyPart0?.Length ?? 0) + (_keyPart1?.Length ?? 0);

    public static bool operator ==(in TrieKey left, in TrieKey right) => left.Equals(right);
    public static bool operator !=(TrieKey left, TrieKey right) => !(left == right);

    public readonly bool Equals(TrieKey other)
    {
        if (Length != other.Length) return false;

        ReadOnlySpan<byte> thisSpan0 = _keyPart0;
        ReadOnlySpan<byte> otherSpan0 = other._keyPart0;

        if (thisSpan0.Length == otherSpan0.Length)
        {
            return thisSpan0.SequenceEqual(otherSpan0);
        }

        ReadOnlySpan<byte> thisSpan1 = _keyPart1 ?? default;
        ReadOnlySpan<byte> otherSpan1 = other._keyPart1 ?? default;

        int thisIndex = 0;
        int otherIndex = 0;
        int totalLength = Length;

        while (thisIndex < totalLength && otherIndex < totalLength)
        {
            ReadOnlySpan<byte> thisCurrentSpan = thisIndex < thisSpan0.Length ? thisSpan0 : thisSpan1;
            int thisCurrentIndex = thisIndex < thisSpan0.Length ? thisIndex : thisIndex - thisSpan0.Length;

            ReadOnlySpan<byte> otherCurrentSpan = otherIndex < otherSpan0.Length ? otherSpan0 : otherSpan1;
            int otherCurrentIndex = otherIndex < otherSpan0.Length ? otherIndex : otherIndex - otherSpan0.Length;

            int thisRemaining = thisCurrentSpan.Length - thisCurrentIndex;
            int otherRemaining = otherCurrentSpan.Length - otherCurrentIndex;
            int compareLength = Math.Min(thisRemaining, otherRemaining);

            if (!thisCurrentSpan.Slice(thisCurrentIndex, compareLength).SequenceEqual(otherCurrentSpan.Slice(otherCurrentIndex, compareLength)))
            {
                return false;
            }

            thisIndex += compareLength;
            otherIndex += compareLength;
        }

        return true;
    }

    public readonly override bool Equals(object obj) => obj is TrieKey key && key.Equals(obj);
    public readonly override int GetHashCode() => throw new NotImplementedException();
}
