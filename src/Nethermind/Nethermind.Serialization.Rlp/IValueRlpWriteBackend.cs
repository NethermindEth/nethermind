// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Write target for <see cref="ValueRlpWriter{TBackend}"/>.
/// </summary>
/// <remarks>
/// Implementations receive already-encoded RLP bytes. They may write to caller-owned spans, pooled buffers, hash
/// accumulators, or other streaming targets. <see cref="ValueRlpWriter{TBackend}.Dispose"/> disposes the backend.
/// </remarks>
public interface IValueRlpWriteBackend : IDisposable
{
    /// <summary>
    /// Full caller-provided output span.
    /// </summary>
    Span<byte> Data => throw new InvalidOperationException("Data is available only for span-backed writers.");

    /// <summary>
    /// Bytes written so far.
    /// </summary>
    ReadOnlySpan<byte> WrittenSpan => throw new InvalidOperationException("WrittenSpan is available only for span-backed writers.");

    /// <summary>
    /// Current write position in <see cref="Data"/>.
    /// </summary>
    int Position
    {
        get => throw new InvalidOperationException("Position and Length are unavailable for non-span-backed writers.");
        set => throw new InvalidOperationException("Position and Length are unavailable for non-span-backed writers.");
    }

    /// <summary>
    /// Length of the span-backed output buffer.
    /// </summary>
    int Length => throw new InvalidOperationException("Position and Length are unavailable for non-span-backed writers.");

    /// <summary>
    /// Writes a single encoded RLP byte.
    /// </summary>
    void WriteByte(byte byteToWrite);

    /// <summary>
    /// Writes encoded RLP bytes.
    /// </summary>
    void Write(scoped ReadOnlySpan<byte> bytesToWrite);

    /// <summary>
    /// Writes zero bytes.
    /// </summary>
    void WriteZero(int length);

    /// <summary>
    /// Span-backed RLP write backend.
    /// </summary>
    public ref struct SpanBackend : IValueRlpWriteBackend
    {
        private Span<byte> _data;
        private int _position;

        /// <summary>
        /// Initializes a backend over a caller-owned output span.
        /// </summary>
        public SpanBackend(Span<byte> data)
        {
            _data = data;
            _position = 0;
        }

        /// <summary>
        /// Full caller-provided output span.
        /// </summary>
        public readonly Span<byte> Data => _data;

        /// <summary>
        /// Bytes written so far.
        /// </summary>
        public readonly ReadOnlySpan<byte> WrittenSpan => _data[.._position];

        /// <summary>
        /// Current write position.
        /// </summary>
        public int Position
        {
            readonly get => _position;
            set => _position = value;
        }

        /// <summary>
        /// Length of the span-backed output buffer.
        /// </summary>
        public readonly int Length => _data.Length;

        /// <summary>
        /// Writes a single encoded RLP byte.
        /// </summary>
        public void WriteByte(byte byteToWrite) => _data[_position++] = byteToWrite;

        /// <summary>
        /// Writes encoded RLP bytes.
        /// </summary>
        public void Write(scoped ReadOnlySpan<byte> bytesToWrite)
        {
            bytesToWrite.CopyTo(_data.Slice(_position, bytesToWrite.Length));
            _position += bytesToWrite.Length;
        }

        /// <summary>
        /// Writes zero bytes.
        /// </summary>
        public void WriteZero(int length)
        {
            _data.Slice(_position, length).Clear();
            _position += length;
        }

        /// <summary>
        /// Span-backed output is caller-owned, so disposing is a no-op.
        /// </summary>
        public void Dispose()
        {
        }

        /// <inheritdoc/>
        public override readonly string ToString() => $"[{nameof(ValueRlpWriter<IValueRlpWriteBackend.CustomBackend>)}|{_position}/{Length}]";
    }

    /// <summary>
    /// DotNetty buffer-backed RLP write backend.
    /// </summary>
    public struct ByteBufferBackend : IValueRlpWriteBackend
    {
        private IByteBuffer _byteBuffer;
        private int _position;

        /// <summary>
        /// Initializes a backend over a DotNetty buffer.
        /// </summary>
        public ByteBufferBackend(IByteBuffer byteBuffer)
        {
            ArgumentNullException.ThrowIfNull(byteBuffer);

            _byteBuffer = byteBuffer;
            _position = 0;
        }

        /// <summary>
        /// Bytes written through this backend.
        /// </summary>
        public int Position
        {
            readonly get => _position;
            set => throw new InvalidOperationException("ByteBuffer-backed writer position cannot be reassigned.");
        }

        /// <summary>
        /// Writes a single encoded RLP byte.
        /// </summary>
        public void WriteByte(byte byteToWrite)
        {
            _byteBuffer.EnsureWritable(1);
            _byteBuffer.WriteByte(byteToWrite);
            _position++;
        }

        /// <summary>
        /// Writes encoded RLP bytes.
        /// </summary>
        public void Write(scoped ReadOnlySpan<byte> bytesToWrite)
        {
            _byteBuffer.EnsureWritable(bytesToWrite.Length);
            if (_byteBuffer.HasArray)
            {
                Span<byte> target = _byteBuffer.Array.AsSpan(_byteBuffer.ArrayOffset + _byteBuffer.WriterIndex, bytesToWrite.Length);
                bytesToWrite.CopyTo(target);
                _byteBuffer.SetWriterIndex(_byteBuffer.WriterIndex + bytesToWrite.Length);
            }
            else
            {
                for (int i = 0; i < bytesToWrite.Length; i++)
                {
                    _byteBuffer.WriteByte(bytesToWrite[i]);
                }
            }

            _position += bytesToWrite.Length;
        }

        /// <summary>
        /// Writes zero bytes.
        /// </summary>
        public void WriteZero(int length)
        {
            _byteBuffer.EnsureWritable(length);
            _byteBuffer.WriteZero(length);
            _position += length;
        }

        /// <summary>
        /// DotNetty buffer ownership remains with the caller, so disposing is a no-op.
        /// </summary>
        public readonly void Dispose()
        {
        }

        /// <inheritdoc/>
        public override readonly string ToString() => $"[{nameof(ValueRlpWriter<IValueRlpWriteBackend.CustomBackend>)}|{_byteBuffer.GetType().Name}|{_position}]";
    }

    /// <summary>
    /// Keccak accumulator-backed RLP write backend.
    /// </summary>
    public struct KeccakBackend : IValueRlpWriteBackend
    {
        private KeccakHash _keccakHash;
        private int _position;

        /// <summary>
        /// Initializes a backend over a Keccak accumulator.
        /// </summary>
        public KeccakBackend(KeccakHash keccakHash)
        {
            ArgumentNullException.ThrowIfNull(keccakHash);

            _keccakHash = keccakHash;
            _position = 0;
        }

        /// <summary>
        /// Bytes written through this backend.
        /// </summary>
        public int Position
        {
            readonly get => _position;
            set => throw new InvalidOperationException("Keccak-backed writer position cannot be reassigned.");
        }

        /// <summary>
        /// Writes a single encoded RLP byte.
        /// </summary>
        public void WriteByte(byte byteToWrite)
        {
            Span<byte> singleByte = stackalloc byte[1] { byteToWrite };
            _keccakHash.Update(singleByte);
            _position++;
        }

        /// <summary>
        /// Writes encoded RLP bytes.
        /// </summary>
        public void Write(scoped ReadOnlySpan<byte> bytesToWrite)
        {
            _keccakHash.Update(bytesToWrite);
            _position += bytesToWrite.Length;
        }

        /// <summary>
        /// Writes zero bytes.
        /// </summary>
        public void WriteZero(int length)
        {
            int originalLength = length;
            Span<byte> zeros = stackalloc byte[Math.Min(length, 256)];
            zeros.Clear();
            while (length > 0)
            {
                int chunkLength = Math.Min(length, zeros.Length);
                _keccakHash.Update(zeros[..chunkLength]);
                length -= chunkLength;
            }
            _position += originalLength;
        }

        /// <summary>
        /// Keccak accumulator ownership remains with the caller, so disposing is a no-op.
        /// </summary>
        public readonly void Dispose()
        {
        }

        /// <inheritdoc/>
        public override readonly string ToString() => $"[{nameof(ValueRlpWriter<IValueRlpWriteBackend.CustomBackend>)}|{_keccakHash.GetType().Name}|{_position}]";
    }

    /// <summary>
    /// Interface-backed custom RLP write backend.
    /// </summary>
    public struct CustomBackend : IValueRlpWriteBackend
    {
        private IValueRlpWriteBackend? _backend;
        private int _position;

        /// <summary>
        /// Initializes a backend over a custom writer. Disposing this backend disposes the custom writer.
        /// </summary>
        public CustomBackend(IValueRlpWriteBackend backend)
        {
            ArgumentNullException.ThrowIfNull(backend);

            _backend = backend;
            _position = 0;
        }

        /// <summary>
        /// Bytes written through this backend.
        /// </summary>
        public int Position
        {
            readonly get => _position;
            set => throw new InvalidOperationException("Custom writer position cannot be reassigned.");
        }

        /// <summary>
        /// Writes a single encoded RLP byte.
        /// </summary>
        public void WriteByte(byte byteToWrite)
        {
            _backend!.WriteByte(byteToWrite);
            _position++;
        }

        /// <summary>
        /// Writes encoded RLP bytes.
        /// </summary>
        public void Write(scoped ReadOnlySpan<byte> bytesToWrite)
        {
            _backend!.Write(bytesToWrite);
            _position += bytesToWrite.Length;
        }

        /// <summary>
        /// Writes zero bytes.
        /// </summary>
        public void WriteZero(int length)
        {
            _backend!.WriteZero(length);
            _position += length;
        }

        /// <summary>
        /// Disposes the custom writer.
        /// </summary>
        public void Dispose()
        {
            _backend?.Dispose();
            _backend = null;
        }

        /// <inheritdoc/>
        public override readonly string ToString() =>
            $"[{nameof(ValueRlpWriter<IValueRlpWriteBackend.CustomBackend>)}|{_backend?.GetType().Name ?? "disposed"}|{_position}]";
    }
}
