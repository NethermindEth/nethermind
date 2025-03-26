using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CommunityToolkit.HighPerformance;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Newtonsoft.Json.Converters;

namespace Nethermind.Logs;

public class LogsBuilder
{
    /// <summary>
    /// Represents an encoded pair of (block, tx) numbers, stored under one uint.
    /// </summary>
    public readonly struct Entry : IEquatable<Entry>
    {
        private const uint MaxTxNumber = 1 << BlockNumberShift;
        private const uint MaxBlockNumber = uint.MaxValue / MaxTxNumber;

        private readonly uint _raw;

        public Entry(uint raw)
        {
            _raw = raw;
        }

        public Entry(uint blockNumber, uint txNumber) : this((blockNumber << BlockNumberShift) | txNumber)
        {
            Debug.Assert(txNumber < MaxTxNumber);
            Debug.Assert(blockNumber < MaxBlockNumber);
        }

        public uint TxNumber => _raw & TxMask;

        public uint BlockNumber => _raw >> BlockNumberShift;

        public bool Equals(Entry other) => _raw == other._raw;

        public override string ToString() => $"{nameof(TxNumber)}: {TxNumber} @ {nameof(BlockNumber)}: {BlockNumber}";
    }

    private readonly ref struct Searcher<TBucketing> where TBucketing : IBucketing
    {
        private const int OffsetU32 = sizeof(uint);
        private const int OffsetU16 = sizeof(ushort);
        private const int OffsetU8 = sizeof(byte);

        private readonly uint _u32;
        private readonly ushort _u16;
        private readonly byte _u8;

        private Searcher(ReadOnlySpan<byte> trimmed)
        {
            _u32 = MemoryMarshal.Read<uint>(trimmed);
            trimmed = trimmed.Slice(OffsetU32);

            if (trimmed.Length >= OffsetU16)
            {
                _u16 = MemoryMarshal.Read<ushort>(trimmed);
                trimmed = trimmed.Slice(OffsetU16);
            }

            if (trimmed.Length == OffsetU8)
            {
                _u8 = trimmed[0];
            }
        }

        private bool Matches(in byte r)
        {
            if (Unsafe.ReadUnaligned<uint>(in r) != _u32) return false;
            if (Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref Unsafe.AsRef(in  r), OffsetU32)) != _u16) return false;

            const int bytes6 = OffsetU32 + OffsetU16;
            return TBucketing.TrimmedLength == bytes6 || Unsafe.Add(ref Unsafe.AsRef(in  r), bytes6) == _u8;
        }

        public static uint FindEntry(ReadOnlySpan<byte> hashesAndPrefixes, ulong hash)
        {
            var trimmed = TBucketing.TrimBucket(ref hash);

            var searcher = new Searcher<TBucketing>(trimmed);

            var length = hashesAndPrefixes.Length;

            var offset = 0;

            while (offset < length)
            {
                // jump over the trimmed to decode the entry
                var read = BinaryEncoding.TryReadVarInt(hashesAndPrefixes, offset + TBucketing.TrimmedLength, out var entry);

                if (searcher.Matches(in hashesAndPrefixes[offset]))
                {
                    return entry;
                }

                offset += read + TBucketing.TrimmedLength;
            }

            return NotFound;
        }
    }

    public sealed class MemoryReader<TBucketing>(
        ReadOnlyMemory<byte> prefixes,
        ReadOnlyMemory<byte> hashesAndPointers,
        ReadOnlyMemory<byte> compressed) : IReader
        where TBucketing : IBucketing
    {
        public int MaxBucketLength
        {
            get
            {
                var lastOffset = 0;
                var maxDiff = 0;

                var buckets = MemoryMarshal.Cast<byte, int>(prefixes.Span);
                for (var bucket = 0; bucket < TBucketing.Count; bucket++)
                {
                    var diff = buckets[bucket] - lastOffset;
                    maxDiff = Math.Max(diff, maxDiff);
                    lastOffset = buckets[bucket];
                }

                return maxDiff;
            }
        }

        public IEnumerable<Entry> Find(Address address) => FindByHash(Hash(address.Bytes, AddressSeed));

        public IEnumerable<Entry> Find(Hash256 topic, int index = 0) =>
            FindByHash(Hash(topic.Bytes, GetTopicSeed(index)));

        private IEnumerable<Entry> FindByHash(ulong hash)
        {
            var (from, length) = FindRangeInPrefixes(prefixes, hash);

            if (length == 0)
                return [];

            var entries = hashesAndPointers.Span.Slice(from, length);

            var m = Searcher<TBucketing>.FindEntry(entries, hash);

            if (m == NotFound)
                return [];

            if ((m & LookupMarker) == 0)
            {
                return [new(m >> LookupMarkerShift)];
            }

            var start = (int)(m >> LookupMarkerShift);

            ReadOnlyMemory<byte> payload = compressed.Slice(start);
            return new EntryEnumerable(payload);

            // As all the bucketing uses int based encoding for buckets, we can get it here.
            static (int from, int length) FindRangeInPrefixes(ReadOnlyMemory<byte> prefixes, ulong hash)
            {
                var buckets = MemoryMarshal.Cast<byte, int>(prefixes.Span);
                var index = TBucketing.GetBucket(hash);

                if (index == 0)
                {
                    return (0, buckets[0]);
                }

                var from = buckets[index - 1];
                return (from, buckets[index] - from);
            }
        }

        private sealed class EntryEnumerable(ReadOnlyMemory<byte> payload) : IEnumerable<Entry>
        {
            public IEnumerator<Entry> GetEnumerator()
            {
                var offset = 0;
                var accumulator = 0u;

                while (offset < payload.Length)
                {
                    var read = BinaryEncoding.TryReadVarInt(payload.Span, offset, out var diff);

                    if (read == -1)
                        throw new InvalidOperationException();

                    if (diff == Terminator)
                    {
                        // Reached the end
                        break;
                    }

                    accumulator += diff;
                    offset += read;

                    yield return new Entry(accumulator);
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    public interface IReader
    {
        IEnumerable<Entry> Find(Address address);
        IEnumerable<Entry> Find(Hash256 topic, int index = 0);
    }

    public sealed class FileReader<TBucketing>(
        SafeFileHandle prefixes,
        SafeFileHandle hashesAndPointers,
        SafeFileHandle compressed) : IReader where TBucketing : IBucketing
    {
        public IEnumerable<Entry> Find(Address address) => FindByHash(Hash(address.Bytes, AddressSeed));

        public IEnumerable<Entry> Find(Hash256 topic, int index = 0) =>
            FindByHash(Hash(topic.Bytes, GetTopicSeed(index)));

        private IEnumerable<Entry> FindByHash(ulong hash)
        {
            var (from, length) = FindRangeInPrefixes(prefixes, hash);

            if (length == 0)
                return [];

            var buffer = ArrayPool<byte>.Shared.Rent(length);
            var span = buffer.AsSpan(0, length);

            RandomAccess.Read(hashesAndPointers, span, from);

            var m = Searcher<TBucketing>.FindEntry(span, hash);

            if (m == NotFound)
                return [];

            if ((m & LookupMarker) == 0)
            {
                return [new(m >> LookupMarkerShift)];
            }

            var start = (int)(m >> LookupMarkerShift);

            const int bucketSize = sizeof(uint);
            return new EntryEnumerable(compressed, start);

            // As all the bucketing uses int based encoding for buckets, we can get it here.
            static (int from, int length) FindRangeInPrefixes(SafeFileHandle prefixes, ulong hash)
            {
                var index = TBucketing.GetBucket(hash);

                Span<byte> span = stackalloc byte[2 * bucketSize];

                int to;
                int from = 0;

                if (index == 0)
                {
                    // Read once, the 0th index
                    RandomAccess.Read(prefixes, span, 0);
                    to = Unsafe.ReadUnaligned<int>(ref span[0]);
                }
                else
                {
                    // Read once, the 0th index
                    RandomAccess.Read(prefixes, span, (index - 1) * bucketSize);
                    from = Unsafe.ReadUnaligned<int>(ref span[0]);
                    to = Unsafe.ReadUnaligned<int>(ref span[bucketSize]);
                }

                return (from, to - from);
            }
        }

        private sealed class EntryEnumerable : IEnumerable<Entry>, IDisposable
        {
            private readonly FileStream _stream;
            private readonly byte[] _buffer;

            private const int BufferSize = 4096;

            public EntryEnumerable(SafeFileHandle compressed, int start)
            {
                _stream = new FileStream(compressed, FileAccess.Read, BufferSize, false);
                _stream.Position = start;
                _buffer = new byte[BinaryEncoding.MaxVarIntByteCount];
            }

            public IEnumerator<Entry> GetEnumerator()
            {
                var accumulator = 0u;
                var leftover = 0;

                while (true)
                {
                    var toRead = BinaryEncoding.MaxVarIntByteCount - leftover;
                    var ready = _stream.Read(_buffer, leftover, toRead) + leftover;

                    var read = BinaryEncoding.TryReadVarInt(_buffer.AsSpan(0, ready), 0, out var diff);

                    if (read == -1)
                        throw new InvalidOperationException();

                    if (diff == Terminator)
                    {
                        // Reached the end
                        break;
                    }

                    accumulator += diff;
                    leftover = ready - read;

                    _buffer.AsSpan(read).CopyTo(_buffer);

                    var e = new Entry(accumulator);
                    yield return e;
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public void Dispose() => _stream.Dispose();
        }
    }

    private const int SizeOfMixed = sizeof(int);
    private const uint NotFound = uint.MaxValue;

    private const long AddressSeed = 0;
    private const long TopicSeed = 1;

    /// <summary>
    /// The bit shift of the block number that ensures that entries that appear every block, can be written in a dense way.
    ///
    /// To ensure it we encode transactions to 12 bits numbers. This allows for consecutive blocks
    /// to have this value different only by 13 bits. 13 bit number can be written with <see cref="BinaryEncoding.WriteVarInt"/>
    /// using only 2 bytes instead of three.
    /// </summary>
    private const int BlockNumberShift = 12;

    /// <summary>
    /// Max block number taking into consideration <see cref="LookupMarker"/>
    /// that requires the highest bit.
    /// </summary>
    private const int MaxBlockNumber = (1 << (31 - BlockNumberShift)) - 1;

    private const int MinBlockNumber = 0;

    private const int MaxTxPerBlock = 1 << BlockNumberShift;
    private const int TxMask = MaxTxPerBlock - 1;

    /// <summary>
    /// We use the smallest bit to put the lookup marker. For smaller values it can save some bytes.
    /// </summary>
    private const int LookupMarkerShift = 1;

    private const uint LookupMarker = 0x01;

    private const int Size = 1024 * 1024;

    private ulong[] _hashes = new ulong[Size];
    private uint[] _mixed = new uint[Size];
    private int _index;

    public int Count => _index;

    public void Append(LogEntry entry, uint blockNumber, ushort txNumber)
    {
        Debug.Assert(txNumber < MaxTxPerBlock);
        Debug.Assert(MinBlockNumber <= blockNumber && blockNumber <= MaxBlockNumber);

        uint mixed = (blockNumber << BlockNumberShift) | txNumber;

        AppendRaw(entry, mixed);
    }

    public void AppendRaw(LogEntry entry, uint mixed)
    {
        // Append address
        Append(entry.Address.Bytes, AddressSeed, mixed);

        for (var i = 0; i < entry.Topics.Length; i++)
        {
            Hash256 topic = entry.Topics[i];
            Append(topic.Bytes, GetTopicSeed(i), mixed);
        }
    }

    private void Append(ReadOnlySpan<byte> payload, long seed, uint mixed)
    {
        if (_hashes.Length == _index)
        {
            Grow();
        }

        _hashes[_index] = Hash(payload, seed);
        _mixed[_index] = mixed;
        _index++;
    }

    private static ulong Hash(ReadOnlySpan<byte> payload, long seed) => XxHash64.HashToUInt64(payload, seed);

    public void Reset()
    {
        _index = 0;
    }

    private void Grow()
    {
        Array.Resize(ref _hashes, _hashes.Length + Size);
        Array.Resize(ref _mixed, _hashes.Length + Size);
    }

    private static long GetTopicSeed(int i) => TopicSeed + i;


    public void Build<TBucketing>(IBufferWriter<byte> prefixes, IBufferWriter<byte> hashesAndPointers,
        IBufferWriter<byte> compressed)
        where TBucketing : IBucketing
    {
        var w = new CountingBufferWriter<byte>(compressed);

        // Sort by hash first
        Array.Sort(_hashes, _mixed, 0, _index);

        var count = Deduplicate(w);

        var buckets = TBucketing.Count;

        // The hash index
        var at = 0;
        var hashes = new CountingBufferWriter<byte>(hashesAndPointers);

        for (var bucket = 0; bucket < buckets; bucket++)
        {
            if (at >= count)
            {
                // no more hashes to process
                prefixes.WriteNativeEndian(hashes.WrittenCount);
                continue;
            }

            var current = TBucketing.GetBucket(_hashes[at]);

            if (current > bucket)
            {
                prefixes.WriteNativeEndian(hashes.WrittenCount);
                continue;
            }

            Debug.Assert(current == bucket);

            for (; at < count; at++)
            {
                if (TBucketing.GetBucket(_hashes[at]) != bucket)
                    break;

                // trim and write entry
                var trimmed = TBucketing.TrimBucket(ref _hashes[at]);

                var required = TBucketing.TrimmedLength + BinaryEncoding.MaxVarIntByteCount;

                var span = hashes.GetSpan(required);
                trimmed.CopyTo(span);
                var written = BinaryEncoding.WriteVarInt(_mixed[at], span.Slice(TBucketing.TrimmedLength));

                hashes.Advance(TBucketing.TrimmedLength + written);
            }

            prefixes.WriteNativeEndian(hashes.WrittenCount);
        }
    }

    private int Deduplicate(CountingBufferWriter<byte> writer)
    {
        // Values are sorted by the hashes, so that two entries that share a hash would be next to each other.
        // We walk through the entries, gathering hashes that are the same and compressing them by writing the hash only once

        var writeAt = 0; // Where we place the next "deduplicated" entry

        var hashes = _hashes.AsSpan(0, _index);
        var mixed = _mixed.AsSpan(0, _index);

        while (hashes.IsEmpty == false)
        {
            var hash = hashes[0];
            var end = hashes.IndexOfAnyExcept(hash);

            if (end == -1)
            {
                // This is the value till the end.
                // Set the end to the mixed length, so that it's handled by cases below
                end = mixed.Length;
            }

            if (end == 1)
            {
                // A single hash occurrence
                _hashes[writeAt] = hash;
                _mixed[writeAt] = mixed[0] << LookupMarkerShift;
            }
            else if (end > 1)
            {
                // Multiple entries with the same hash
                _hashes[writeAt] = hash;
                _mixed[writeAt] = CompressMixed(writer, mixed.Slice(0, end));
            }

            hashes = hashes.Slice(end);
            mixed = mixed.Slice(end);
            writeAt++;
        }

        return writeAt;
    }

    private static uint CompressMixed(CountingBufferWriter<byte> writer, Span<uint> values)
    {
        // Previous might not be stably sorted. Ensure it.
        values.Sort();

        // Remember the starting position
        var start = writer.WrittenCount;

        var previous = 0U;
        var written = 0;

        Span<byte> span = default;

        // Simple diff encoding
        foreach (var value in values)
        {
            var diff = value - previous;

            // Skip repeated entries
            if (diff == 0) continue;

            if (span.Length - written < BinaryEncoding.MaxVarIntByteCount)
            {
                if (written > 0)
                {
                    writer.Advance(written);
                    written = 0;
                }

                span = writer.GetSpan(BinaryEncoding.MaxVarIntByteCount);
            }

            written += BinaryEncoding.WriteVarInt(diff, span, written);
            previous = value;
        }

        // Advance the leftover
        if (written > 0)
        {
            writer.Advance(written);
        }

        // Write the terminator value, that marks the end of the sequence by putting 0 at the end.
        writer.Write(BinaryEncoding.Zero);

        // Write the start position with the marker
        return ((uint)start << LookupMarkerShift) | LookupMarker;
    }

    private const int Terminator = 0;

    private const int BitsPerByte = 8;

    public interface IBucketing
    {
        /// <summary>
        /// The actual length of span after <see cref="TrimBucket"/>
        /// </summary>
        public static abstract int TrimmedLength { get; }

        public static abstract int Count { get; }

        public static abstract int GetBucket(ulong hash);

        public static abstract ReadOnlySpan<byte> TrimBucket(ref ulong hash);
    }

    public readonly struct Bucket3Bytes : IBucketing
    {
        private const int ByteCount = 3;

        public static int TrimmedLength => sizeof(ulong) - ByteCount;
        public static int Count => 1 << (ByteCount * BitsPerByte);

        public static int GetBucket(ulong hash) => (int)(hash >> ((sizeof(ulong) - ByteCount) * BitsPerByte));

        public static ReadOnlySpan<byte> TrimBucket(ref ulong hash)
        {
            var span = MemoryMarshal.Cast<ulong, byte>(MemoryMarshal.CreateReadOnlySpan(ref hash, 1));

            // Endian dependent slicing
            return BitConverter.IsLittleEndian ? span.Slice(0, sizeof(ulong) - ByteCount) : span.Slice(ByteCount);
        }
    }

    public readonly struct Bucket2Bytes : IBucketing
    {
        private const int ByteCount = 2;

        public static int TrimmedLength => sizeof(ulong) - ByteCount;
        public static int Count => 1 << (ByteCount * BitsPerByte);

        public static int GetBucket(ulong hash) => (int)(hash >> ((sizeof(ulong) - ByteCount) * BitsPerByte));

        public static ReadOnlySpan<byte> TrimBucket(ref ulong hash)
        {
            var span = MemoryMarshal.Cast<ulong, byte>(MemoryMarshal.CreateReadOnlySpan(ref hash, 1));

            // Endian dependent slicing
            return BitConverter.IsLittleEndian ? span.Slice(0, sizeof(ulong) - ByteCount) : span.Slice(ByteCount);
        }
    }

    public readonly struct Bucket1Bytes : IBucketing
    {
        private const int ByteCount = 1;

        public static int TrimmedLength => sizeof(ulong) - ByteCount;
        public static int Count => 1 << (ByteCount * BitsPerByte);

        public static int GetBucket(ulong hash) => (int)(hash >> ((sizeof(ulong) - ByteCount) * BitsPerByte));

        public static ReadOnlySpan<byte> TrimBucket(ref ulong hash)
        {
            var span = MemoryMarshal.Cast<ulong, byte>(MemoryMarshal.CreateReadOnlySpan(ref hash, 1));

            // Endian dependent slicing
            return BitConverter.IsLittleEndian ? span.Slice(0, sizeof(ulong) - ByteCount) : span.Slice(ByteCount);
        }
    }
}
