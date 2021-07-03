//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 
// #define JUMP_TABLE

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Db.Files
{
    public class FileDb : IDb
    {
        private readonly ConcurrentDictionary<byte[], byte[]> _toFlush = new(new KeccakKeyComparer());
        private readonly ConcurrentQueue<byte[]> _flushQueue = new();
        private readonly Chunk[] _chunks = new Chunk[1024];
        private volatile int _lastChunk = -1;
        private static readonly byte[] _empty = new byte[0];

        // current chunk
        private const int DefaultPayloadSize = 512 * 1024 * 1024;
        private readonly long[] _prefixes;
        private readonly int[] _offsets;
        private readonly byte[] _bytes;
        private int _writtenBytes;
        private int _writtenKeys;
        private readonly Dictionary<long, int> _transientWritePrefixes;
        private readonly Thread _flusher;
        private readonly CancellationTokenSource _cts = new();
        public string FullPath { get; }

        public FileDb(string basePath, string name, int size = DefaultPayloadSize, bool recreateOnStart = false)
        {
            Name = name;

            _bytes = GC.AllocateUninitializedArray<byte>(size, true);

            int maxEntryCount = size / Chunk.KeySize;
            _transientWritePrefixes = new(maxEntryCount);
            _offsets = new int[maxEntryCount];
            _prefixes = new long[maxEntryCount];

            FullPath = name.GetApplicationResourcePath(basePath);

            if (Directory.Exists(FullPath))
            {
                if (recreateOnStart)
                {
                    Directory.Delete(FullPath, true);
                    Directory.CreateDirectory(FullPath);
                }
            }
            else
            {
                Directory.CreateDirectory(FullPath);
            }

            _flusher = new Thread(Work);
            _flusher.Start();
        }

        private void Work()
        {
            List<ValueTuple<long, int>> offsets = new();
            List<byte[]> keys = new();

            while (!_cts.IsCancellationRequested || !_flushQueue.IsEmpty)
            {
                if (_flushQueue.IsEmpty)
                {
                    Thread.Sleep(50);
                    continue;
                }

                int position = _writtenBytes;
                Span<byte> log = _bytes.AsSpan(position);

                bool shouldFlush = false;

                while (_flushQueue.TryPeek(out var key))
                {
                    byte[] value = _toFlush[key];
                    int length = Chunk.KeySize + Chunk.LengthSize + value.Length;

                    if (log.Length > length)
                    {
                        _flushQueue.TryDequeue(out _); // already peeked

                        long prefix = ReadPrefix(key);

                        // remember the write first in all helper structures
                        keys.Add(key);
                        offsets.Add((prefix, position));
                        _prefixes[_writtenKeys] = prefix;
                        _offsets[_writtenKeys] = position;
                        _writtenKeys++;

                        // copy the data
                        key.CopyTo(log);
                        log = log[Chunk.KeySize..];

                        Unsafe.WriteUnaligned(ref log[0], (short)value.Length);
                        log = log[Chunk.LengthSize..];

                        value.CopyTo(log);
                        log = log[value.Length..];

                        // adjust position
                        position += length;
                    }
                    else
                    {
                        shouldFlush = true;
                        break;
                    }
                }

                // remember the position
                _writtenBytes = position;

                // write transient
                lock (_transientWritePrefixes)
                {
                    foreach ((long prefix, int offset) in offsets)
                    {
                        _transientWritePrefixes[prefix] = offset;
                    }
                }

                offsets.Clear();

                if (shouldFlush)
                {
                    // sort first
                    _prefixes.AsSpan(0, _writtenKeys).Sort(_offsets.AsSpan(0, _writtenKeys));

                    int chunk = _lastChunk + 1;
                    FileStream file = new(Path.Combine(FullPath, chunk.ToString("D8") + ".db"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);

                    // long to make it 8 bytes long
                    long aligned = ((_writtenBytes >> 3) + 1) << 3;

                    // 1. preamble with length
                    file.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref aligned, 1)));

                    // 2. actual bytes
                    file.Write(_bytes, 0, (int)aligned);

                    // 3. prefixes
                    file.Write(MemoryMarshal.AsBytes(_prefixes.AsSpan(0, _writtenKeys)));

                    // 4. offsets
                    file.Write(MemoryMarshal.AsBytes(_offsets.AsSpan(0, _writtenKeys)));

                    file.Flush(true);

                    _chunks[chunk] = new Chunk(file);
                    _lastChunk = chunk;

                    _writtenBytes = 0;
                    _writtenKeys = 0;

                    lock (_transientWritePrefixes)
                    {
                        _transientWritePrefixes.Clear();
                    }
                }

                // removed flushed after the file is flushed
                foreach (byte[] key in keys)
                {
                    _toFlush.TryRemove(key, out _);
                }

                keys.Clear();
            }
        }

        public byte[]? this[byte[] key]
        {
            get => TryGet(key, out byte[]? value) ? value : null;
            set => Put(key, value ?? _empty);
        }

        public IBatch StartBatch() => new Batch(this);

        public void Dispose()
        {
            _cts.Cancel();
            _flusher.Join();

            int i = _lastChunk;
            while (i >= 0)
            {
                _chunks[i].Dispose();
                i--;
            }
        }

        public bool KeyExists(byte[] key) => TryGet(key, out _);

        void Put(byte[] key, byte[] value)
        {
            if (key.Length != Chunk.KeySize)
            {
                throw new ArgumentException("Invalid length", nameof(key));
            }

            _toFlush[key] = value;
            _flushQueue.Enqueue(key);
        }

        [SkipLocalsInit]
        bool TryGet(byte[] key, out byte[]? value)
        {
            if (_toFlush.TryGetValue(key, out value))
            {
                return true;
            }

            long prefix = ReadPrefix(key);

            Span<byte> bytes = stackalloc byte[1024];
            short length = -1;

            // try the current
            lock (_transientWritePrefixes)
            {
                if (_transientWritePrefixes.TryGetValue(prefix, out int offset))
                {
                    length = Unsafe.ReadUnaligned<short>(ref _bytes[offset + Chunk.KeySize]);
                    _bytes.AsSpan(offset + 2, length).CopyTo(bytes);
                }
            }

            if (length >= 0)
            {
                // allocate outside of the lock
                value = bytes.Slice(0, length).ToArray();
                return true;
            }

            // not found, scan all, from the latest
            int i = _lastChunk;
            while (i >= 0)
            {
                if (_chunks[i].TryGet(prefix, key, out value))
                {
                    return true;
                }

                i--;
            }

            return false;
        }

        public string Name { get; }

        public bool HasWritesPending => !_flushQueue.IsEmpty;

        public IDb Innermost => this;

        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => throw new NotImplementedException();
        public IEnumerable<byte[]> GetAllValues(bool ordered) => throw new NotImplementedException();
        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => throw new NotImplementedException();
        public void Remove(byte[] key) => throw new NotImplementedException();
        public void Flush() => throw new NotImplementedException();
        public void Clear() => throw new NotImplementedException();

        static long ReadPrefix(byte[] key) => Unsafe.ReadUnaligned<long>(ref key[0]);

        class Batch : IBatch
        {
            private readonly FileDb _db;
            private readonly ConcurrentDictionary<byte[], byte[]> _values = new(new KeccakKeyComparer());

            public Batch(FileDb db)
            {
                _db = db;
            }

            public void Dispose()
            {
                foreach (KeyValuePair<byte[], byte[]> kvp in _values)
                {
                    _db[kvp.Key] = kvp.Value;
                }
                _values.Clear();
            }

            public byte[]? this[byte[] key]
            {
                get
                {
                    if (_values.TryGetValue(key, out byte[]? v))
                    {
                        return v;
                    }
                    if (_db.TryGet(key, out v))
                    {
                        return v;
                    }

                    return null;
                }
                set => _values[key] = value ?? _empty;
            }
        }

        /// <summary>
        /// Chunks are written in the following way:
        /// - 4 bytes to describe the length of payload
        /// - payload as tuples of (key, 2 bytes for length, value)
        /// - n prefixes (8 bytes - long)
        /// - n offsets  (4 bytes - int)
        /// 
        /// </summary>
        class Chunk : IDisposable
        {
            public const int PreambleLength = sizeof(long);
            public const int KeySize = 32;
            public const int LengthSize = sizeof(short);
            public const int OffsetSize = sizeof(int);
            public const int PrefixSize = sizeof(long);
            public const int OsPageSize = 1024 * 4;
            public const int JumpLength = OsPageSize / PrefixSize;

            private readonly MemoryMappedFile _mmf;
            private readonly MemoryMappedViewAccessor _accessor;
            private readonly int _jump;
            private readonly int _count;
            private readonly SafeMemoryMappedViewHandle _handle;

#if JUMP_TABLE
            private readonly long[] _skips;
#endif

            public Chunk(FileStream stream)
            {
                int length = (int)stream.Length;
                _mmf = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
                _accessor = _mmf.CreateViewAccessor(0L, 0L, MemoryMappedFileAccess.Read);
                _handle = _accessor.SafeMemoryMappedViewHandle;
                _jump = (int)_accessor.ReadInt64(0);
                _count = (length - PreambleLength - _jump) / (PrefixSize + OffsetSize);

#if JUMP_TABLE
                // create a fast skip list
                _skips = new long[_count / JumpLength];
                unsafe
                {
                    byte* ptr = null;
                    _handle.AcquirePointer(ref ptr);

                    try
                    {
                        ptr += PreambleLength;
                        byte* start = ptr + _jump;

                        for (int i = 0; i < _skips.Length; i++)
                        {
                            _skips[i] = Unsafe.Read<long>(start + i * JumpLength * PrefixSize);
                        }
                    }
                    finally
                    {
                        if ((IntPtr)ptr != IntPtr.Zero)
                            _handle.ReleasePointer();
                    }
                }
#endif
            }

            [SkipLocalsInit]
            public unsafe bool TryGet(long prefix, byte[] key, out byte[]? value)
            {
                byte* ptr = null;
                _handle.AcquirePointer(ref ptr);

                try
                {
                    ptr += PreambleLength;
                    byte* start = ptr + _jump;

#if JUMP_TABLE
                    // calculate lowest boundary
                    int lo = 0;
                    while (lo < _skips.Length - 1 && _skips[lo + 1] < prefix)
                    {
                        lo++;
                    }

                    // calculate highest boundary
                    int jump = lo * JumpLength;
                    
                    // got one more jump? try it whether it's bigger than prefix then jump no more than a single jump in search
                    int count = ((lo < _skips.Length - 1) && prefix < _skips[lo + 1]) ? JumpLength : _count - jump;
#else
                    const int jump = 0;
                    int count = _count;
#endif
                    int index = Helpers.BinarySearch(ref Unsafe.AsRef<long>(start + jump * PrefixSize), count, prefix);
                    index += jump;
                    if (index >= 0)
                    {
                        // prefixes and offsets are aligned to memory boundary
                        int offset = Unsafe.Read<int>(start +
                                                               (_count * PrefixSize) + // skip all the prefixes
                                                               (index * OffsetSize));  // skip till the index

                        ref byte k = ref key[0];

                        if (Unsafe.Read<long>(ptr + offset + 8) != Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref k, 8)) ||
                            Unsafe.Read<long>(ptr + offset + 16) != Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref k, 16)) ||
                            Unsafe.Read<long>(ptr + offset + 24) != Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref k, 24)))
                        {
                            value = default;
                            return false;
                        }

                        short length = Unsafe.ReadUnaligned<short>(ptr + offset + KeySize);
                        value = new Span<byte>(ptr + offset + KeySize + LengthSize, length).ToArray();
                        return true;
                    }
                }
                finally
                {
                    if ((IntPtr)ptr != IntPtr.Zero)
                        _handle.ReleasePointer();
                }

                value = default;
                return false;
            }

            public void Dispose()
            {
                _accessor.Dispose();
                _mmf.Dispose();
            }
        }
    }
}
