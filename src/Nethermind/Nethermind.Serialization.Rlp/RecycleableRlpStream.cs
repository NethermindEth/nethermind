// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;

using Microsoft.IO;

using Nethermind.Core.Resettables;

namespace Nethermind.Serialization.Rlp
{
    public sealed class RecyclableRlpStream : RlpStream, IDisposable
    {
        private readonly RecyclableMemoryStream _buffer;

        public RecyclableRlpStream(RecyclableMemoryStream buffer)
        {
            _buffer = buffer;
        }

        public RecyclableRlpStream(string tag)
        {
            _buffer = RecyclableStream.GetStream(tag);
        }

        public override void Write(ReadOnlySpan<byte> bytesToWrite)
        {
            _buffer.Write(bytesToWrite);
        }

        public override void Write(IReadOnlyList<byte> bytesToWrite)
        {
            for (int i = 0; i < bytesToWrite.Count; ++i)
            {
                _buffer.WriteByte(bytesToWrite[i]);
            }
        }

        public override void WriteByte(byte byteToWrite)
        {
            _buffer.WriteByte(byteToWrite);
        }

        protected override void WriteZero(int length)
        {
            Span<byte> zeros = stackalloc byte[length];
            _buffer.Write(zeros);
        }

        public override byte ReadByte()
        {
            var b = _buffer.ReadByte();
            if (b < 0)
            {
                ThrowReadPassedEndOfStream();
            }
            return (byte)b;
        }

        public override Span<byte> Read(int length)
        {
            if (_buffer.TryGetBuffer(out ArraySegment<byte> buffer))
            {
                Span<byte> data = buffer.Array.AsSpan(buffer.Offset + (int)_buffer.Position, length);
                _buffer.Position += length;
                return data;
            }

            return ReadLonger(length);
        }

        private Span<byte> ReadLonger(int length)
        {
            ReadOnlySequence<byte> sequence = _buffer.GetReadOnlySequence().Slice(_buffer.Position, length);
            _buffer.Position += length;
            ReadOnlyMemory<byte> memory = sequence.First;
            if (memory.Length <= length)
            {
                return Unsafe.As<ReadOnlyMemory<byte>, Memory<byte>>(ref memory).Span.Slice(0, length);
            }

            return sequence.ToArray();
        }

        public override Span<byte> Peek(int offset, int length)
        {
            long position = _buffer.Position;
            _buffer.Position = position + offset;
            Span<byte> data = Read(length);
            _buffer.Position = position;
            return data;
        }

        public override byte PeekByte()
        {
            int b = _buffer.ReadByte();
            if (b < 0)
            {
                ThrowReadPassedEndOfStream();
            }
            _buffer.Position--;
            return (byte)b;
        }

        protected override byte PeekByte(int offset)
        {
            _buffer.Position += offset;
            byte b = PeekByte();
            _buffer.Position -= offset;

            return b;
        }

        protected override void SkipBytes(int length)
        {
            _buffer.Position += length;
        }

        public override int Position
        {
            get => (int)_buffer.Position;
            set => _buffer.Position = value;
        }

        public override int Length => (int)_buffer.Length;

        public override bool HasBeenRead => _buffer.Position > 0;

        protected override string Description => "|RecyclableRlpStream|description missing|";

        public void CopyTo(Stream stream)
        {
            _buffer.Position = 0;
            _buffer.CopyTo(stream);
        }

        public void Dispose()
        {
            _buffer.Dispose();
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowReadPassedEndOfStream()
        {
            throw new EndOfStreamException("Read passed end of stream");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private void ThrowWriteOnlyException()
            => throw new NotSupportedException($"Cannot read from {nameof(RecyclableRlpStream)}");
    }
}
