using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Logs;

public class LogsBuilder
{
    public readonly struct Entry : IEquatable<Entry>
    {
        private readonly uint _raw;

        public Entry(uint raw)
        {
            _raw = raw;
        }

        public Entry(uint blockNumber, uint txNumber) : this((blockNumber << BlockNumberShift) | txNumber)
        {
        }

        public uint TxNumber => _raw & TxMask;

        public uint BlockNumber => _raw >> BlockNumberShift;

        public bool Equals(Entry other) => _raw == other._raw;
    }

    public sealed class MemoryReader
    {
        private readonly ReadOnlyMemory<byte> _mixed;
        private readonly ReadOnlyMemory<byte> _hashes;
        private readonly ReadOnlyMemory<byte> _memory;

        public MemoryReader(ReadOnlyMemory<byte> memory)
        {
            _memory = memory;
            var count = memory.Span.Slice(memory.Length - LengthOfLength).ReadNativeEndian();

            var startOfEntries = (int)(memory.Length - LengthOfLength - count * EntryWithHash);
            var mixedWithHashes = memory.Slice(startOfEntries, (int)(count * EntryWithHash));

            Debug.Assert(mixedWithHashes.Length == count * EntryWithHash);

            var split = count * SizeOfMixed;

            _mixed = mixedWithHashes.Slice(0, split);
            _hashes = mixedWithHashes.Slice(split);
        }

        public IEnumerable<Entry> Find(Address address) => FindByHash(Hash(address.Bytes, AddressSeed));

        public IEnumerable<Entry> Find(Hash256 topic, int index = 0) =>
            FindByHash(Hash(topic.Bytes, GetTopicSeed(index)));

        private IEnumerable<Entry> FindByHash(ulong hash)
        {
            var at = MemoryMarshal.Cast<byte, ulong>(_hashes.Span).BinarySearch(hash);

            if (at < 0)
                return [];

            ReadOnlySpan<uint> mixed = MemoryMarshal.Cast<byte, uint>(_mixed.Span);

            var m = mixed[at];

            if ((m & LookupMarker) == 0)
            {
                return [new(m)];
            }

            var offset = (int)(m & ~LookupMarker);
            var start = _memory[offset..].Span.ReadNativeEndian();

            ReadOnlyMemory<byte> payload = _memory.Slice(start, offset - start);
            return new EntryEnumerable(payload);
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

                    accumulator += diff;
                    offset += read;

                    yield return new Entry(accumulator);
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    /// <summary>
    /// The length of length written at the end.
    /// </summary>
    public const int LengthOfLength = 4;

    private const int SizeOfHash = sizeof(ulong);
    private const int SizeOfMixed = sizeof(int);

    public const int EntryWithHash = SizeOfHash + SizeOfMixed;

    public const int CompressedEntryLength = sizeof(int);
    public const int CompressedEntrySize = sizeof(int);

    private const long AddressSeed = 0;
    private const long TopicSeed = 1;

    /// <summary>
    /// The shift for the block ensures that there is one bit (sign) left in the mixed to use as a marker
    /// </summary>
    private const int BlockNumberShift = 14;

    /// <summary>
    /// Max block number taking into consideration <see cref="LookupMarker"/>
    /// that requires the highest bit.
    /// </summary>
    private const int MaxBlockNumber = (1 << (31 - BlockNumberShift)) - 1;

    private const int MinBlockNumber = 1;

    private const int MaxTxPerBlock = 1 << BlockNumberShift;
    private const int TxMask = MaxTxPerBlock - 1;
    private const uint LookupMarker = 0x80_00_00_00;

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

    public void Build(IBufferWriter<byte> data)
    {
        var w = new CountingBufferWriter<byte>(data);

        // Sort by hash first
        Array.Sort(_hashes, _mixed, 0, _index);

        var count = Deduplicate(w);

        // Mixed first
        w.WriteNativeEndianSpan<uint>(_mixed.AsSpan(0, count));

        // Hashes then
        w.WriteNativeEndianSpan<ulong>(_hashes.AsSpan(0, count));

        // Store it as the last one, so that the reader can read last 4 bytes and create proper span.
        w.WriteNativeEndian(count);
    }

    private int Deduplicate(CountingBufferWriter<byte> writer)
    {
        // Values are sorted by the hashes, so that two entries that share a hash would be next to each other.
        // We walk through the entries, gathering hashes that are the same and compressing them by writing the hash only once

        int writeAt = 0; // Where we place the next "deduplicated" entry
        ulong currentHash = _hashes[0]; // The hash value we’re currently accumulating
        uint currentMixed = _mixed[0]; // The mixed value for the first occurrence
        int currentCount = 1; // How many times we've seen currentHash so far

        // Scan through all array elements, starting from index 1:
        for (int i = 1; i < _index; i++)
        {
            if (_hashes[i] == currentHash)
            {
                // Found another occurrence of the same hash:
                currentCount++;
            }
            else
            {
                // We reached a new hash => finalize the block for the previous one
                _hashes[writeAt] = currentHash;
                if (currentCount == 1)
                {
                    // If there was only 1 occurrence, keep the original mixed
                    _mixed[writeAt] = currentMixed;
                }
                else
                {
                    CompressMixed(writer, i, currentCount, writeAt);
                }

                writeAt++;

                // Now start tracking the new hash block
                currentHash = _hashes[i];
                currentMixed = _mixed[i];
                currentCount = 1;
            }
        }

        // After the loop, finalize the last block
        _hashes[writeAt] = currentHash;
        if (currentCount == 1)
        {
            _mixed[writeAt] = currentMixed;
        }
        else
        {
            CompressMixed(writer, _index, currentCount, writeAt);
        }

        return writeAt + 1;
    }

    private void CompressMixed(CountingBufferWriter<byte> writer, int to, int count, int writeAt)
    {
        Span<uint> values = _mixed.AsSpan(to - count, count);

        // Previous might not be stably sorted. Ensure it.
        values.Sort();

        // Remember the starting position
        var start = writer.WrittenCount;

        var previous = 0U;

        // Simple diff encoding
        foreach (var value in values)
        {
            var diff = value - previous;

            // diff == 0 when it's a repeated entry. Skip these
            if (diff > 0)
            {
                // TODO: optimize span getting and advancing.
                Span<byte> span = writer.GetSpan(5);
                var written = BinaryEncoding.WriteVarInt(diff, span, 0);
                writer.Advance(written);
            }

            previous = value;
        }

        var end = writer.WrittenCount;

        writer.WriteNativeEndian(start);

        // Write the marker
        _mixed[writeAt] = LookupMarker | (uint)end;
    }
}
