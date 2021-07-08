////  Copyright (c) 2021 Demerzel Solutions Limited
////  This file is part of the Nethermind library.
//// 
////  The Nethermind library is free software: you can redistribute it and/or modify
////  it under the terms of the GNU Lesser General Public License as published by
////  the Free Software Foundation, either version 3 of the License, or
////  (at your option) any later version.
//// 
////  The Nethermind library is distributed in the hope that it will be useful,
////  but WITHOUT ANY WARRANTY; without even the implied warranty of
////  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
////  GNU Lesser General Public License for more details.
//// 
////  You should have received a copy of the GNU Lesser General Public License
////  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//// 

//using System;
//using System.IO;
//using System.Runtime.InteropServices;
//using System.Threading;
//using Nethermind.Core.Extensions;

//namespace Nethermind.Db.Files
//{
//    public class PersistentLog
//    {
//        private static readonly int _chunkSize = (int)256.MiB();
//        private const int MaxChunkCount = 3000;
//        private const int InMemoryChunks = 2;

//        private readonly Chunk[] _chunks = new Chunk[MaxChunkCount];
//        private readonly IntPtr[] _buffers = new IntPtr[InMemoryChunks];
//        private int _current;

//        public PersistentLog()
//        {
//            for (int i = 0; i < InMemoryChunks; i++)
//            {
//                _buffers[i] = Marshal.AllocHGlobal(_chunkSize);
//            }
//        }

//        public long Write(byte[] value)
//        {
//            int index = Volatile.Read(ref _current);
//            Chunk chunk = _chunks[index];

//            if (chunk.TryWrite(value, out int offset))
//            {
//                return CreateKey(index, offset);
//            }

//        }

//        static long CreateKey(int chunkIndex, int position) => (((long)chunkIndex) << 32) | (uint)position;

//        class Chunk
//        {
//            private readonly IntPtr _buffer;
//            private readonly FileStream _stream;
//            private int _position;

//            public Chunk(IntPtr buffer, FileStream stream)
//            {
//                _buffer = buffer;
//                _stream = stream;
//            }

//            public bool TryWrite(byte[] value, out int offset)
//            {
//                int length = value.Length;
//                int position = Volatile.Read(ref _position);

//                if (position + length > _chunkSize)
//                {
//                    TrySeal();
//                    offset = default;
//                    return false;
//                }

//                lock (this)
//                {
//                    if (_position + length > _chunkSize)
//                    {
//                        TrySeal();
//                        offset = default;
//                        return false;
//                    }

//                    unsafe
//                    {
//                        value.CopyTo(new Span<byte>((byte*)_buffer.ToPointer() + _position, length));
//                    }

//                    offset = _position;
//                    _position += length;
//                    return true;
//                }

//                Monitor.wa
//            }

//            public void FlushNoLock(bool flushToDisk = false)
//            {
//                int current = Volatile.Read(ref _position);
//                int flushedTo = (int)_stream.Position;

//                if (current > flushedTo)
//                {
//                    unsafe
//                    {
//                        _stream.Write(new Span<byte>(((byte*)_buffer.ToPointer()) + flushedTo, current - flushedTo));
//                        _stream.Flush(flushToDisk);
//                    }

//                    flushedTo = current;
//                }
//            }

//            private void TrySeal()
//            {
//                throw new NotImplementedException();
//            }
//        }
//    }
//}
