// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie;

/// <summary>
/// Represents a key used in a trie data structure. 
/// This struct can hold a key in one or two byte array parts 
/// for efficient concatenation and slicing operations.
/// </summary>
public readonly struct TrieKey
{
    // Predefined array instances for single-byte keys (0..15).
    private readonly static byte[][] _singleBytes = [[0], [1], [2], [3], [4], [5], [6], [7], [8], [9], [10], [11], [12], [13], [14], [15]];
    // Predefined TrieKey instances for single-byte keys (0..15).
    private readonly static TrieKey[] _singleByteKeys = [new(_singleBytes[0]), new(_singleBytes[1]), new(_singleBytes[2]), new(_singleBytes[3]), new(_singleBytes[4]), new(_singleBytes[5]), new(_singleBytes[6]), new(_singleBytes[7]), new(_singleBytes[8]), new(_singleBytes[9]), new(_singleBytes[10]), new(_singleBytes[11]), new(_singleBytes[12]), new(_singleBytes[13]), new(_singleBytes[14]), new(_singleBytes[15])];

    /// <summary>
    /// An empty TrieKey instance (no bytes).
    /// </summary>
    public static TrieKey Empty { get; } = new(Array.Empty<byte>());

    // These two byte arrays together represent the key. 
    // If _keyPart1 is null, the entire key is in _keyPart0.
    private readonly byte[]? _keyPart0;
    private readonly byte[]? _keyPart1;

    /// <summary>
    /// Creates a TrieKey from a single byte array.
    /// </summary>
    /// <param name="key">The byte array representing the key.</param>
    public TrieKey(byte[] key) => _keyPart0 = key;

    /// <summary>
    /// Creates a TrieKey by prepending a single byte to another TrieKey.
    /// </summary>
    /// <param name="keyPart0">Single byte to prepend.</param>
    /// <param name="keyPart1">The remaining part of the key as a TrieKey.</param>
    public TrieKey(byte keyPart0, TrieKey keyPart1)
    {
        // If the second part is empty, just store the single byte in _keyPart0.
        if (keyPart1.Length == 0)
        {
            _keyPart0 = _singleBytes[keyPart0];
        }
        // If the second part has only _keyPart0, combine single-byte with that.
        else if (keyPart1._keyPart1 is null)
        {
            _keyPart0 = _singleBytes[keyPart0];
            _keyPart1 = keyPart1._keyPart0;
        }
        else
        {
            // If the second part has two arrays, combine them and prepend the single byte.
            // We often slice off the first byte, so just combine keyPart1 parts
            _keyPart0 = _singleBytes[keyPart0];
            _keyPart1 = Bytes.Concat(keyPart1._keyPart0, keyPart1._keyPart1);
        }
    }

    /// <summary>
    /// Creates a TrieKey by concatenating two TrieKeys.
    /// </summary>
    /// <param name="keyPart0">The first TrieKey segment.</param>
    /// <param name="keyPart1">The second TrieKey segment.</param>
    public TrieKey(TrieKey keyPart0, TrieKey keyPart1)
    {
        // If both parts are empty, this key is empty.
        if (keyPart0.Length == 0 && keyPart1.Length == 0)
        {
            _keyPart0 = Array.Empty<byte>();
        }
        // If the first part is empty, just copy the second part's arrays.
        else if (keyPart0.Length == 0)
        {
            _keyPart0 = keyPart1._keyPart0;
            _keyPart1 = keyPart1._keyPart1;
        }
        // If the second part is empty, just copy the first part's arrays.
        else if (keyPart1.Length == 0)
        {
            _keyPart0 = keyPart0._keyPart0;
            _keyPart1 = keyPart0._keyPart1;
        }
        // If both parts only have _keyPart0 (no _keyPart1), just set them.
        else if (keyPart0._keyPart1 is null && keyPart1._keyPart1 is null)
        {
            _keyPart0 = keyPart0._keyPart0;
            _keyPart1 = keyPart1._keyPart0;
        }
        // If only the first part has a single array, handle partial combination carefully.
        else if (keyPart0._keyPart1 is null)
        {
            // Depending on lengths, combine part or all of the second key's arrays.
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
        // If only the second part has a single array, handle partial combination carefully.
        else if (keyPart1._keyPart1 is null)
        {
            // Depending on lengths, combine part or all of the first key's arrays.
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
            // Both keys have two arrays, so fully concatenate them.
            _keyPart0 = new byte[keyPart0.Length];
            Array.Copy(keyPart0._keyPart0, 0, _keyPart0, 0, keyPart0._keyPart0.Length);
            Array.Copy(keyPart0._keyPart1, 0, _keyPart0, keyPart0._keyPart0.Length, keyPart0._keyPart1.Length);

            _keyPart1 = new byte[keyPart1.Length];
            Array.Copy(keyPart1._keyPart0, 0, _keyPart1, 0, keyPart1._keyPart0.Length);
            Array.Copy(keyPart1._keyPart1, 0, _keyPart1, keyPart1._keyPart0.Length, keyPart1._keyPart1.Length);
        }
    }

    /// <summary>
    /// Creates a TrieKey from two separate byte arrays.
    /// </summary>
    /// <param name="keyPart0">The first part of the key.</param>
    /// <param name="keyPart1">The second part of the key.</param>
    public TrieKey(byte[] keyPart0, byte[] keyPart1)
    {
        _keyPart0 = keyPart0;
        _keyPart1 = keyPart1;
    }

    /// <summary>
    /// Implicitly converts a single byte into a TrieKey.
    /// </summary>
    /// <param name="key">A single byte.</param>
    public static implicit operator TrieKey(byte key) => _singleByteKeys[key];

    /// <summary>
    /// Implicitly converts a byte array into a TrieKey.
    /// </summary>
    /// <param name="key">A byte array.</param>
    public static implicit operator TrieKey(byte[] key) => new(key);

    /// <summary>
    /// Gets the byte at the specified index in this TrieKey.
    /// </summary>
    /// <param name="index">Index of the byte to retrieve.</param>
    /// <returns>The byte at the specified index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if index is out of range (less than 0 or >= Length).
    /// </exception>
    public readonly byte this[int index]
    {
        get
        {
            if (index < 0 || index >= Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index out of range");
            }
            // If index is within the first array, return from _keyPart0; otherwise from _keyPart1.
            return index < (_keyPart0?.Length ?? 0)
                ? _keyPart0![index]
                : _keyPart1![index - _keyPart0!.Length];
        }
    }

    /// <summary>
    /// Calculates the length of the common prefix between this TrieKey and another.
    /// </summary>
    /// <param name="other">Another TrieKey to compare.</param>
    /// <returns>The number of bytes of the shared prefix.</returns>
    public readonly int CommonPrefixLength(TrieKey other)
    {
        ReadOnlySpan<byte> a0 = _keyPart0;
        ReadOnlySpan<byte> b0 = other._keyPart0;
        // Single-array shortcut: if both keys have only _keyPart0, compare directly.
        if (_keyPart1 is null && other._keyPart1 is null)
        {
            return a0.CommonPrefixLength(b0);
        }

        // Set up spans for each part:
        ReadOnlySpan<byte> a1 = _keyPart1;
        ReadOnlySpan<byte> b1 = other._keyPart1;

        // We'll do at most two segments for "this" (a0 then a1) and two for "other" (b0 then b1).
        // The loop ends as soon as we find a mismatch in the middle of either segment
        // or run out of segments on either side.

        int totalMatched = 0;

        // Current spans for each side
        ReadOnlySpan<byte> curA = a0;
        ReadOnlySpan<byte> curB = b0;

        // Which part are we on? false => part0, true => part1
        bool aIsOnPart1 = false;
        bool bIsOnPart1 = false;

        while (true)
        {
            // 1) Compare the current spans with .CommonPrefixLength
            int prefix = curA.CommonPrefixLength(curB);
            totalMatched += prefix;

            // 2) Check if we exhausted one or both spans
            bool aExhausted = prefix == curA.Length;
            bool bExhausted = prefix == curB.Length;

            // 2a) If mismatch in the middle of both spans => done
            if (!aExhausted && !bExhausted)
            {
                return totalMatched;
            }

            // 2b) If both are exhausted simultaneously, move both to their next part
            if (aExhausted && bExhausted)
            {
                // Move "this" from a0 -> a1 if not already there
                if (!aIsOnPart1)
                {
                    curA = a1;
                    aIsOnPart1 = true;
                }
                else
                {
                    // Already on a1 => nothing more to compare
                    return totalMatched;
                }

                // Move "other" from b0 -> b1 if not already there
                if (!bIsOnPart1)
                {
                    curB = b1;
                    bIsOnPart1 = true;
                }
                else
                {
                    // Already on b1 => nothing more to compare
                    return totalMatched;
                }

                // Now compare new spans in next loop iteration
                continue;
            }

            // 2c) If exactly one is exhausted, keep leftover in the other
            //     and move the exhausted side to its next part if possible.

            // "this" side exhausted => move it to a1, or done if we're already on a1
            if (aExhausted && !bExhausted)
            {
                if (!aIsOnPart1)
                {
                    // Move this side from a0 -> a1
                    curA = a1;
                    aIsOnPart1 = true;
                }
                else
                {
                    // Already on a1 => nothing left
                    return totalMatched;
                }
                // On the other side, we slice off the matched portion
                curB = curB.Slice(prefix);

                // Continue comparing leftover in b's current part vs a1
                continue;
            }

            // "other" side exhausted => move it to b1, or done if already on b1
            if (bExhausted && !aExhausted)
            {
                if (!bIsOnPart1)
                {
                    // Move other side from b0 -> b1
                    curB = b1;
                    bIsOnPart1 = true;
                }
                else
                {
                    // Already on b1 => nothing left
                    return totalMatched;
                }
                // On this side, we slice off the matched portion
                curA = curA.Slice(prefix);

                // Continue comparing leftover in a's current part vs b1
                continue;
            }

            // If we reach here, it means we can't proceed further
            // (e.g. leftover in one side but no second part in the other).
            return totalMatched;
        }
    }

    /// <summary>
    /// Returns a subset (slice) of this TrieKey starting at a given index through the end.
    /// </summary>
    /// <param name="start">Starting index (inclusive).</param>
    /// <returns>A new TrieKey representing the sliced portion.</returns>
    public readonly TrieKey Slice(int start) => Slice(start, Length - start);

    /// <summary>
    /// Returns a subset (slice) of this TrieKey with the specified start index and length.
    /// </summary>
    /// <param name="start">Starting index (inclusive).</param>
    /// <param name="length">Number of bytes to include in the slice.</param>
    /// <returns>A new TrieKey representing the sliced portion.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if the slice parameters are outside this TrieKey's range.
    /// </exception>
    public readonly TrieKey Slice(int start, int length)
    {
        if (start < 0 || length < 0 || start + length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Invalid start or length parameters");
        }

        // If we request an empty slice, return Empty.
        if (length == 0)
        {
            return Empty;
        }

        // If the slice is exactly 1 byte, we can return a predefined single-byte key.
        if (length == 1)
        {
            return _singleByteKeys[this[start]];
        }

        int part0Length = _keyPart0?.Length ?? 0;

        // If the entire slice is within the first array:
        if (_keyPart1 is null || start + length <= part0Length)
        {
            // If the slice covers the entire first array and there's no second array,
            // or if it effectively covers the entire _keyPart0 portion, return that slice directly.
            if (start == 0 && length == part0Length)
            {
                return _keyPart1 is null ? this : new TrieKey(_keyPart0!);
            }

            // Otherwise, copy out the relevant portion from _keyPart0.
            byte[] newArray = new byte[length];
            Array.Copy(_keyPart0!, start, newArray, 0, length);
            return new TrieKey(newArray);
        }

        // If the slice is entirely in the second array:
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

        // If the slice spans both arrays, copy the relevant parts from each.
        int lengthInPart0 = part0Length - start;
        int lengthInPart1 = length - lengthInPart0;
        byte[] newKey = new byte[length];

        Array.Copy(_keyPart0, start, newKey, 0, lengthInPart0);
        Array.Copy(_keyPart1, 0, newKey, lengthInPart0, lengthInPart1);

        return new TrieKey(newKey);
    }

    /// <summary>
    /// Converts this TrieKey into a single byte array.
    /// </summary>
    /// <returns>A byte array containing the entire key.</returns>
    public readonly byte[] ToArray()
    {
        var length = Length;
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        // If the key is just one byte, we can reference our singleBytes array.
        if (length == 1)
        {
            return _singleBytes[this[0]];
        }

        // If only _keyPart0 is used and it matches the total length, return it.
        if (_keyPart0.Length == length)
        {
            return _keyPart0;
        }

        // Otherwise, combine both parts into one array.
        byte[] result = new byte[length];
        _keyPart0?.CopyTo(result, 0);
        _keyPart1?.CopyTo(result, _keyPart0?.Length ?? 0);
        return result;
    }

    /// <summary>
    /// Gets the total length (in bytes) of this TrieKey.
    /// </summary>
    public readonly int Length => (_keyPart0?.Length ?? 0) + (_keyPart1?.Length ?? 0);

    /// <summary>
    /// Compares two TrieKeys for equality.
    /// </summary>
    /// <param name="left">The left TrieKey.</param>
    /// <param name="right">The right TrieKey.</param>
    /// <returns>True if they have the same bytes; otherwise false.</returns>
    public static bool operator ==(in TrieKey left, in TrieKey right) => left.Equals(in right);

    /// <summary>
    /// Compares two TrieKeys for inequality.
    /// </summary>
    /// <param name="left">The left TrieKey.</param>
    /// <param name="right">The right TrieKey.</param>
    /// <returns>True if they differ in any byte; otherwise false.</returns>
    public static bool operator !=(TrieKey left, TrieKey right) => !(left == right);

    /// <summary>
    /// Indicates whether this instance and a specified TrieKey have the same bytes.
    /// </summary>
    /// <param name="other">The TrieKey to compare with.</param>
    /// <returns>True if they are byte-identical; otherwise false.</returns>
    public readonly bool Equals(in TrieKey other)
    {
        if (Length != other.Length) return false;

        ReadOnlySpan<byte> thisSpan0 = _keyPart0;
        ReadOnlySpan<byte> otherSpan0 = other._keyPart0;

        // If both have the same length in their first array, compare them directly.
        if (thisSpan0.Length == otherSpan0.Length && (_keyPart1 is null || _keyPart1.Length == 0))
        {
            return thisSpan0.SequenceEqual(otherSpan0);
        }

        // Otherwise, we have to account for part1 segments too.
        ReadOnlySpan<byte> thisSpan1 = _keyPart1 ?? default;
        ReadOnlySpan<byte> otherSpan1 = other._keyPart1 ?? default;

        int thisTotalIndex = 0;
        int otherTotalIndex = 0;
        int totalLength = Length;

        // Compare segment by segment across parts 0 and parts 1.
        while (thisTotalIndex < totalLength && otherTotalIndex < totalLength)
        {
            ReadOnlySpan<byte> thisSpan = thisTotalIndex < thisSpan0.Length ? thisSpan0 : thisSpan1;
            int thisIndex = thisTotalIndex < thisSpan0.Length ? thisTotalIndex : thisTotalIndex - thisSpan0.Length;

            ReadOnlySpan<byte> otherSpan = otherTotalIndex < otherSpan0.Length ? otherSpan0 : otherSpan1;
            int otherIndex = otherTotalIndex < otherSpan0.Length ? otherTotalIndex : otherTotalIndex - otherSpan0.Length;

            int thisRemaining = thisSpan.Length - thisIndex;
            int otherRemaining = otherSpan.Length - otherIndex;
            int compareLength = Math.Min(thisRemaining, otherRemaining);

            // Compare slices. If they differ at any point, return false.
            if (!thisSpan.Slice(thisIndex, compareLength)
                .SequenceEqual(otherSpan.Slice(otherIndex, compareLength)))
            {
                return false;
            }

            thisTotalIndex += compareLength;
            otherTotalIndex += compareLength;
        }

        return true;
    }

    public readonly void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException($"Destination span must be at least {Length} bytes long.", nameof(destination));
        }

        int offset = 0;

        // Copy the first part if not null
        if (_keyPart0 is not null)
        {
            _keyPart0.CopyTo(destination.Slice(offset, _keyPart0.Length));
            offset += _keyPart0.Length;
        }

        // Copy the second part if not null
        _keyPart1?.CopyTo(destination.Slice(offset, _keyPart1.Length));
    }

    public readonly override bool Equals(object obj) => obj is TrieKey key && Equals(in key);
    public readonly override int GetHashCode() => throw new NotImplementedException();
}
