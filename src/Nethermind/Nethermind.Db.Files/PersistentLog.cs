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

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Db.Files
{
    public class PersistentLog : IDisposable
    {
        private readonly string _directory;
        private readonly IntPtr _buffer;

        private readonly int _size;
        private readonly int _sizeMask;
        private readonly int _chunkSize;
        private readonly int _chunkMask;

        private long _head; // where the flusher is
        private long _tail; // where the writers are

        private const int MaxChunks = 1024;
        private readonly MemoryMappedViewAccessor?[] _accessors = new MemoryMappedViewAccessor[MaxChunks];
        private readonly MemoryMappedFile?[] _files = new MemoryMappedFile[MaxChunks];
        private readonly CancellationTokenSource _cts;
        private readonly Thread _thread;

        private const int HeaderLength = sizeof(long);
        private const int Alignment = 8;

        /// <summary>
        /// The shift in the value to embed the length.
        /// </summary>
        private const int LengthShift = 16;
        private const int LengthMask = (1 << LengthShift) - 1;

        public PersistentLog(int chunkSize, string directory)
        {
            _directory = directory;
            int log2 = chunkSize.Log2();

            _chunkSize = 1 << log2;
            _chunkMask = _chunkSize - 1;
            _size = _chunkSize * 2;
            _sizeMask = _size - 1;
            _buffer = Helpers.AllocAlignedMemory(_size);

            _cts = new CancellationTokenSource();
            _thread = Start(_cts);
        }

        public long Write(byte[] value)
        {
            int length = value.Length;
            int required = Helpers.Align(length, Alignment) + HeaderLength;

            long tail;
            SpinWait spin = default;
            bool retry;

            do
            {
                retry = false;
                long head = Volatile.Read(ref _head);
                tail = Volatile.Read(ref _tail);

                int available = _size - (int)(tail - head);
                if (required > available)
                {
                    // not enough, try in a while
                    spin.SpinOnce();
                    retry = true;
                    continue;
                }

                // ensure    that writes are aligned to chunk boundaries
                long toChunkEnd = _chunkSize - (tail & _chunkMask);

                if (required > toChunkEnd)
                {
                    if (Interlocked.CompareExchange(ref _tail, tail + toChunkEnd, tail) == tail)
                    {
                        // succeeded, write padding
                        long paddingLength = toChunkEnd - Alignment;
                        WriteHeader(tail, paddingLength);
                    }

                    // another worker added padding
                    retry = true;
                    continue;
                }

            } while (retry || Interlocked.CompareExchange(ref _tail, tail + required, tail) != tail);

            // perform actual write
            unsafe
            {
                // copy value first
                byte* position = (byte*)_buffer.ToPointer() + (tail & _sizeMask);
                Span<byte> destination = new(position + HeaderLength, length);
                value.CopyTo(destination);

                // then write the header to make it atomically visible
                long header = BuildHeader(tail, length);
                Volatile.Write(ref Unsafe.AsRef<long>(position), header);
                return header;
            }
        }

        public unsafe Span<byte> Read(long header)
        {
            int length = GetLength(header);
            long position = header >> LengthShift;
            long chunkIndex = position / _chunkSize;
            long chunkOffset = position & _chunkMask;

            // try memory mapped first
            MemoryMappedViewAccessor? accessor = Volatile.Read(ref _accessors[chunkIndex]);
            if (accessor != null)
            {
                return Read(accessor, chunkOffset, length);
            }

            // try buffer 
            byte* start = (byte*)_buffer.ToPointer() + (position & _sizeMask);

            // materialize before comparing
            byte[] array = new Span<byte>(start + HeaderLength, length).ToArray();

            long read = Volatile.Read(ref Unsafe.AsRef<long>(start));

            if (read == header)
            {
                // the ordering is preserved, nothing has overwritten the value in the mean time
                return array;
            }

            // it must have been being mapped, retry till it happens
            SpinWait spin = default;
            while (accessor == null)
            {
                spin.SpinOnce();
                accessor = Volatile.Read(ref _accessors[chunkIndex]);
            }

            return Read(accessor, chunkOffset, length);
        }

        public static int GetLength(long header) => (int)(LengthMask & header);

        private static unsafe Span<byte> Read(MemoryMappedViewAccessor accessor, long chunkOffset, int length)
        {
            IntPtr handle = accessor.SafeMemoryMappedViewHandle.DangerousGetHandle();
            return new Span<byte>((byte*)handle.ToPointer() + chunkOffset + HeaderLength, length);
        }

        Thread Start(CancellationTokenSource cts)
        {
            Thread thread = new(() =>
            {
                SpinWait spin = default;

                while (!cts.IsCancellationRequested)
                {
                    long head = Volatile.Read(ref _head);
                    long tail = Volatile.Read(ref _tail);

                    if ((tail - head) > _chunkSize)
                    {
                        unsafe
                        {
                            long chunkIndex = head / _chunkSize;

                            string file = Path.Combine(_directory, chunkIndex.ToString("D8") + ".log");
                            using (FileStream fileStream =
                                new(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
                            {
                                byte* start = (byte*)_buffer.ToPointer() + (head & _sizeMask);
                                Span<byte> toWrite = new(start, _chunkSize);
                                fileStream.Write(toWrite);
                                fileStream.Flush(true);
                            }
                            MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(file);
                            _files[chunkIndex] = mmf;
                            Volatile.Write(ref _accessors[chunkIndex], mmf.CreateViewAccessor());

                            // write head
                            Volatile.Write(ref _head, _head + _chunkSize);
                        }
                    }
                    spin.SpinOnce();
                }
            });
            thread.Start();
            return thread;
        }

        private unsafe void WriteHeader(long tail, long length)
        {
            long header = BuildHeader(tail, length);
            byte* pointer = (byte*)_buffer.ToPointer() + (tail & _sizeMask);

            Volatile.Write(ref Unsafe.AsRef<long>(pointer), header);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long BuildHeader(long tail, long length) => (tail << LengthShift) + length;

        public void Dispose()
        {
            _cts.Cancel();
            _thread.Join();

            foreach (MemoryMappedViewAccessor? accessor in _accessors)
            {
                accessor?.Dispose();
            }

            foreach (MemoryMappedFile? file in _files)
            {
                file?.Dispose();
            }

            Helpers.FreeAlignedMemory(_buffer);
        }
    }
}
