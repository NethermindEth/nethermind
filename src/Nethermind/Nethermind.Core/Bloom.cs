// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.Core;

[JsonConverter(typeof(BloomConverter))]
public class Bloom : IEquatable<Bloom>
{
    public static readonly Bloom Empty = new();
    public const int BitLength = 2048;
    public const int ByteLength = BitLength / 8;

    private BloomData _bloomData;

    public Bloom() { }

    public Bloom(Bloom?[] blooms) : this()
    {
        if (blooms is null) return;

        for (int i = 0; i < blooms.Length; i++)
        {
            Accumulate(blooms[i]);
        }
    }

    public Bloom(LogEntry[]? logEntries)
    {
        if (logEntries is null) return;

        Add(logEntries);
    }

    public Bloom(LogEntry[]? logEntries, Bloom blockBloom)
    {
        if (logEntries is null) return;

        Add(logEntries, blockBloom);
    }

    public Bloom(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Bytes);
    }

    public Span<byte> Bytes => _bloomData.AsSpan();
    private Span<ulong> ULongs => _bloomData.AsULongs();

    public void Set(ReadOnlySpan<byte> sequence)
    {
        if (ReferenceEquals(this, Empty))
        {
            ThrowInvalidUpdate();
        }

        BloomExtract indexes = GetExtract(sequence);

        // Compute word/masks once.
        (int w1, ulong m1) = WordMask(indexes.Index1);
        (int w2, ulong m2) = WordMask(indexes.Index2);
        (int w3, ulong m3) = WordMask(indexes.Index3);

        // Merge masks if same word to minimize stores.
        if (w2 == w1) { m1 |= m2; w2 = -1; }
        if (w3 == w1) { m1 |= m3; w3 = -1; }
        else if (w3 == w2) { m2 |= m3; w3 = -1; }

        // Write to this bloom using 64-bit words.
        SetWordMasks(w1, m1, w2, m2, w3, m3);
    }

    private void Set(ReadOnlySpan<byte> sequence, Bloom masterBloom)
    {
        if (ReferenceEquals(this, Empty))
        {
            ThrowInvalidUpdate();
        }

        BloomExtract indexes = GetExtract(sequence);

        // Compute word/masks once.
        (int w1, ulong m1) = WordMask(indexes.Index1);
        (int w2, ulong m2) = WordMask(indexes.Index2);
        (int w3, ulong m3) = WordMask(indexes.Index3);

        // Merge masks if same word to minimize stores.
        if (w2 == w1) { m1 |= m2; w2 = -1; }
        if (w3 == w1) { m1 |= m3; w3 = -1; }
        else if (w3 == w2) { m2 |= m3; w3 = -1; }

        // Write to this bloom using 64-bit words.
        SetWordMasks(w1, m1, w2, m2, w3, m3);

        // Write to master bloom using bit indexes.
        masterBloom.SetWordMasks(w1, m1, w2, m2, w3, m3);
    }

    public bool Matches(ReadOnlySpan<byte> sequence) => Matches(GetExtract(sequence));

    public override string ToString() => Bytes.ToHexString();

    public static bool operator !=(Bloom? a, Bloom? b)
    {
        return !(a == b);
    }

    public static bool operator ==(Bloom? a, Bloom? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        return a?.Equals(b) ?? false;
    }

    public bool Equals(Bloom? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((Bloom)obj);
    }

    public override int GetHashCode() => Bytes.FastHash();

    public void Add(LogEntry[] logEntries)
    {
        for (int entryIndex = 0; entryIndex < logEntries.Length; entryIndex++)
        {
            LogEntry logEntry = logEntries[entryIndex];
            byte[] addressBytes = logEntry.Address.Bytes;
            Set(addressBytes);
            Hash256[] topics = logEntry.Topics;
            for (int topicIndex = 0; topicIndex < topics.Length; topicIndex++)
            {
                Hash256 topic = topics[topicIndex];
                Set(topic.Bytes);
            }
        }
    }

    public void Add(LogEntry[] logEntries, Bloom blockBloom)
    {
        for (int entryIndex = 0; entryIndex < logEntries.Length; entryIndex++)
        {
            LogEntry logEntry = logEntries[entryIndex];
            byte[] addressBytes = logEntry.Address.Bytes;
            Set(addressBytes, blockBloom);
            Hash256[] topics = logEntry.Topics;
            for (int topicIndex = 0; topicIndex < topics.Length; topicIndex++)
            {
                Hash256 topic = topics[topicIndex];
                Set(topic.Bytes, blockBloom);
            }
        }
    }

    public void Accumulate(Bloom? bloom)
    {
        if (bloom is null)
        {
            return;
        }

        Bytes.Or(bloom.Bytes);
    }

    public bool Matches(LogEntry logEntry)
    {
        if (Matches(logEntry.Address))
        {
            Hash256[] topics = logEntry.Topics;
            for (int topicIndex = 0; topicIndex < topics.Length; topicIndex++)
            {
                if (!Matches(topics[topicIndex]))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int index)
    {
        (int w, ulong m) = WordMask(index);
        return (ULongs[w] & m) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index)
    {
        (int w, ulong m) = WordMask(index);
        ULongs[w] |= m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetWordMasks(int w1, ulong m1, int w2, ulong m2, int w3, ulong m3)
    {
        Span<ulong> words = ULongs; // 256 / 8 = 32 ulongs
        words[w1] |= m1;
        if (w2 >= 0) words[w2] |= m2;
        if (w3 >= 0) words[w3] |= m3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int w, ulong m) WordMask(int bitIndex)
    {
        int w = bitIndex >> 6;
        int bi = bitIndex & 63;
        int shift = BitConverter.IsLittleEndian ? (bi ^ 7) : (bi ^ 63);
        ulong m = 1UL << shift;
        return (w, m);
    }

    public bool Matches(Address address) => Matches(address.Bytes);

    public bool Matches(Hash256 topic) => Matches(topic.Bytes);

    public bool Matches(BloomExtract extract) => Get(extract.Index1) && Get(extract.Index2) && Get(extract.Index3);

    public static BloomExtract GetExtract(Address address) => GetExtract(address.Bytes);

    public static BloomExtract GetExtract(Hash256 topic) => GetExtract(topic.Bytes);

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static BloomExtract GetExtract(ReadOnlySpan<byte> sequence)
    {
        ref byte k = ref MemoryMarshal.GetReference(ValueKeccak.Compute(sequence).BytesAsSpan);
        ulong u = Unsafe.ReadUnaligned<ulong>(ref k);

        if (BitConverter.IsLittleEndian)
        {
            u = BinaryPrimitives.ReverseEndianness(u);
        }

        return new BloomExtract(u);
    }

    public readonly struct BloomExtract(ulong indexes)
    {
        public int Index1 => 2047 - (int)((indexes >> 48) & 0x07FF);
        public int Index2 => 2047 - (int)((indexes >> 32) & 0x07FF);
        public int Index3 => 2047 - (int)((indexes >> 16) & 0x07FF);

        public bool IsZero() => indexes == 0;
    }

    public BloomStructRef ToStructRef() => new(Bytes);

    public Bloom Clone()
    {
        Bloom clone = new();
        Bytes.CopyTo(clone.Bytes);
        return clone;
    }

    [InlineArray(ByteLength)]
    public struct BloomData
    {
        private byte _element0;
        public Span<byte> AsSpan() => MemoryMarshal.CreateSpan(ref _element0, ByteLength);
        public Span<ulong> AsULongs() => MemoryMarshal.CreateSpan(ref Unsafe.As<byte, ulong>(ref _element0), ByteLength / sizeof(ulong));
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidUpdate()
        => throw new InvalidOperationException("An attempt was made to update Bloom.Empty constant");
}

public ref struct BloomStructRef
{
    public const int BitLength = 2048;
    public const int ByteLength = BitLength / 8;

    public BloomStructRef(ReadOnlySpan<byte> bytes)
    {
        Bytes = bytes;
    }

    public ReadOnlySpan<byte> Bytes { get; }

    public readonly bool Matches(ReadOnlySpan<byte> sequence)
        => Matches(Bloom.GetExtract(sequence));

    public override readonly string ToString() => Bytes.ToHexString();

    public static bool operator !=(BloomStructRef a, Bloom b) => !(a == b);
    public static bool operator ==(BloomStructRef a, Bloom b) => a.Equals(b);

    public static bool operator !=(Bloom a, BloomStructRef b) => !(a == b);
    public static bool operator ==(Bloom a, BloomStructRef b) => b.Equals(a);

    public static bool operator !=(BloomStructRef a, BloomStructRef b) => !(a == b);
    public static bool operator ==(BloomStructRef a, BloomStructRef b) => a.Equals(b);


    public readonly bool Equals(Bloom? other)
    {
        if (other is null) return false;
        return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
    }

    public readonly bool Equals(BloomStructRef other) => Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);


    public override readonly bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (obj.GetType() != typeof(BloomStructRef)) return false;
        return Equals((Bloom)obj);
    }

    public override readonly int GetHashCode() => Bytes.FastHash();

    public readonly bool Matches(LogEntry logEntry)
    {
        if (Matches(logEntry.Address))
        {
            Hash256[] topics = logEntry.Topics;
            for (int topicIndex = 0; topicIndex < topics.Length; topicIndex++)
            {
                if (!Matches(topics[topicIndex]))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private readonly bool Get(int index)
    {
        int bytePosition = index / 8;
        int shift = index % 8;
        return Bytes[bytePosition].GetBit(shift);
    }

    public readonly bool Matches(Address address) => Matches(address.Bytes);

    public readonly bool Matches(Hash256 topic) => Matches(topic.Bytes);

    public readonly bool Matches(Bloom.BloomExtract extract) => Get(extract.Index1) && Get(extract.Index2) && Get(extract.Index3);

    public static Bloom.BloomExtract GetExtract(Address address) => Bloom.GetExtract(address.Bytes);

    public static Bloom.BloomExtract GetExtract(Hash256 topic) => Bloom.GetExtract(topic.Bytes);
}
