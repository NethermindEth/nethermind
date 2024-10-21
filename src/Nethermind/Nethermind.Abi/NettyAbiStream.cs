using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Abi
{
    public sealed class NettyAbiStream : IDisposable
    {

        private readonly IByteBuffer _buffer;

        private readonly int _initialPosition;

        public NettyAbiStream(IByteBuffer buffer)
        {
            _buffer = buffer;
            _initialPosition = buffer.ReaderIndex;
        }

        public void Write(ReadOnlySpan<byte> bytesToWrite)
        {
            _buffer.EnsureWritable(bytesToWrite.Length);

            Span<byte> target =
                _buffer.Array.AsSpan(_buffer.ArrayOffset + _buffer.WriterIndex, bytesToWrite.Length);
            bytesToWrite.CopyTo(target);
            int newWriterIndex = _buffer.WriterIndex + bytesToWrite.Length;

            _buffer.SetWriterIndex(newWriterIndex);
        }

        public void Write(IReadOnlyList<byte> bytesToWrite)
        {
            _buffer.EnsureWritable(bytesToWrite.Count);
            Span<byte> target =
                _buffer.Array.AsSpan(_buffer.ArrayOffset + _buffer.WriterIndex, bytesToWrite.Count);
            for (int i = 0; i < bytesToWrite.Count; ++i)
            {
                target[i] = bytesToWrite[i];
            }

            int newWriterIndex = _buffer.WriterIndex + bytesToWrite.Count;
            _buffer.SetWriterIndex(newWriterIndex);
        }

        public void WriteByte(byte byteToWrite)
        {
            _buffer.EnsureWritable(1);
            _buffer.WriteByte(byteToWrite);
        }

        public void WriteZero(int length)
        {
            _buffer.EnsureWritable(length);
            _buffer.WriteZero(length);
        }

        public byte ReadByte()
        {
            return _buffer.ReadByte();
        }

        public Span<byte> Read(int length)
        {
            Span<byte> span = _buffer.Array.AsSpan(_buffer.ArrayOffset + _buffer.ReaderIndex, length);
            _buffer.SkipBytes(span.Length);
            return span;
        }

        public Span<byte> Peek(int offset, int length)
        {
            Span<byte> span = _buffer.Array.AsSpan(_buffer.ArrayOffset + _buffer.ReaderIndex + offset, length);
            return span;
        }

        public byte PeekByte()
        {
            byte result = _buffer.ReadByte();
            _buffer.SetReaderIndex(_buffer.ReaderIndex - 1);
            return result;
        }

        public byte PeekByte(int offset)
        {
            _buffer.MarkReaderIndex();
            _buffer.SkipBytes(offset);
            byte result = _buffer.ReadByte();
            _buffer.ResetReaderIndex();
            return result;
        }

        public void SkipBytes(int length)
        {
            _buffer.SkipBytes(length);
        }

        public int Position
        {
            get => _buffer.ReaderIndex - _initialPosition;
            set => _buffer.SetReaderIndex(_initialPosition + value);
        }

        public int Length => _buffer.ReadableBytes + (_buffer.ReaderIndex - _initialPosition);

        public bool HasBeenRead => _buffer.ReadableBytes <= 0;

        public string Description => "|NettyAibStream|description missing|";

        /// <summary>
        /// Note: this include already read bytes, not just the remaining one.
        /// </summary>
        /// <returns></returns>
        public Span<byte> AsSpan() => _buffer.AsSpan(_initialPosition);

        public void Dispose()
        {
            _buffer.SafeRelease();
        }
    }
}
