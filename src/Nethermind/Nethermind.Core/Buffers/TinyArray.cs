using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nethermind.Core.Buffers;

/// <summary>
/// Non-generic factory entry point for creating tiny arrays with inline payloads.
/// </summary>
public static class TinyArray
{
    /// <summary>
    /// Max length of a tiny array.
    /// </summary>
    public const int MaxLength = 32;

    /// <summary>
    /// Create a tiny array of minimal inline capacity based on source length.
    /// </summary>
    public static ISpanSource Create(int size)
    {
        return (size / 8) switch
        {
            0 => new TinyArrayImpl<Payload8>(size),
            1 => new TinyArrayImpl<Payload16>(size),
            2 => new TinyArrayImpl<Payload24>(size),
            3 => new TinyArrayImpl<Payload32>(size),

            // assumes fixed
            _ => new TinyArrayImpl<PayloadFixed32>(size)
        };
    }

    public static ISpanSource Create(ReadOnlySpan<byte> src)
    {
        return (src.Length / 8) switch
        {
            0 => new TinyArrayImpl<Payload8>(src),
            1 => new TinyArrayImpl<Payload16>(src),
            2 => new TinyArrayImpl<Payload24>(src),
            3 => new TinyArrayImpl<Payload32>(src),

            // assumes fixed
            _ => new TinyArrayImpl<PayloadFixed32>(src)
        };
    }

    private sealed class TinyArrayImpl<TPayload> : ISpanSource
        where TPayload : struct, IPayload
    {
        private TPayload _payload;

        public TinyArrayImpl(int size)
        {
            _payload = default;
            _payload.SetSize(size);
        }

        /// <summary>
        /// Construct from a source span. Throws if length > payload capacity.
        /// </summary>
        public TinyArrayImpl(ReadOnlySpan<byte> src)
        {
            _payload.Load(src);
        }

        public int Length => _payload.Length;

        public bool SequenceEqual(ReadOnlySpan<byte> other)
            => _payload.SequenceEqual(other);

        public int CommonPrefixLength(ReadOnlySpan<byte> other)
            => _payload.Span.CommonPrefixLength(other);

        public Span<byte> Span => _payload.Span;

        public int MemorySize => MemorySizes.ObjectHeaderMethodTable + TPayload.MemorySize;
    }

    /// <summary>
    /// Payload interface: struct providing inline storage and sequence ops.
    /// </summary>
    private interface IPayload
    {
        void SetSize(int size);
        void Load(ReadOnlySpan<byte> src);
        byte Length { get; }
        Span<byte> Span { get; }
        bool SequenceEqual(ReadOnlySpan<byte> other);
        public static abstract int MemorySize { get; }
    }

    /// <summary>
    /// The offset to the value.
    /// </summary>
    private const int ValueOffset = 1;

    [StructLayout(LayoutKind.Sequential)]
    [InlineArray(Size)]
    private struct Payload8 : IPayload
    {
        private byte _data;
        private const int Size = 8;

        public void SetSize(int size)
        {
            _data = (byte)size;
        }

        public void Load(ReadOnlySpan<byte> src)
        {
            _data = (byte)src.Length;
            src.CopyTo(Span);
        }

        public byte Length => _data;

        public Span<byte> Span => MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _data, ValueOffset), _data);

        public bool SequenceEqual(ReadOnlySpan<byte> other)
        {
            bool result;

            var length = Length;
            if (length != other.Length)
            {
                result = false;
                goto Return;
            }

            ref byte first = ref Unsafe.Add(ref _data, ValueOffset);
            ref byte second = ref MemoryMarshal.GetReference(other);

            if (length < sizeof(uint))
            {
                uint differentBits = 0;
                int offset = length & 2;
                if (offset != 0)
                {
                    differentBits = Unsafe.ReadUnaligned<ushort>(ref first);
                    differentBits -= Unsafe.ReadUnaligned<ushort>(ref second);
                }
                if ((length & 1) != 0)
                {
                    differentBits |= (uint)Unsafe.AddByteOffset(ref first, offset) - (uint)Unsafe.AddByteOffset(ref second, offset);
                }

                result = differentBits == 0;
            }
            else
            {
                int offset = length - sizeof(uint);

                uint differentBits = Unsafe.ReadUnaligned<uint>(ref first) - Unsafe.ReadUnaligned<uint>(ref second);
                differentBits |= Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref first, offset)) - Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref second, offset));

                result = differentBits == 0;
            }

            Return:
            return result;
        }

        public static int MemorySize => Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    [InlineArray(Size)]
    private struct Payload16 : IPayload
    {
        private byte _data;
        private const int Size = 16;

        public void SetSize(int size)
        {
            _data = (byte)size;
        }

        public void Load(ReadOnlySpan<byte> src)
        {
            _data = (byte)src.Length;
            src.CopyTo(Span);
        }

        public byte Length => _data;

        public Span<byte> Span => MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _data, ValueOffset), _data);

        public bool SequenceEqual(ReadOnlySpan<byte> other)
        {
            var length = Length;
            if (length != other.Length)
            {
                return false;
            }

            ref byte first = ref Unsafe.Add(ref _data, ValueOffset);
            ref byte second = ref MemoryMarshal.GetReference(other);

            int offset = length - sizeof(long);

            ulong differentBits = Unsafe.ReadUnaligned<ulong>(ref first) - Unsafe.ReadUnaligned<ulong>(ref second);
            differentBits |= Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, offset)) - Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref second, offset));

            return differentBits == 0;
        }

        public static int MemorySize => Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    [InlineArray(Size)]
    private struct Payload24 : IPayload
    {
        private byte _data;
        private const int Size = 24;

        public void SetSize(int size)
        {
            _data = (byte)size;
        }

        public void Load(ReadOnlySpan<byte> src)
        {
            _data = (byte)src.Length;
            src.CopyTo(Span);
        }

        public byte Length => _data;

        public Span<byte> Span => MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _data, ValueOffset), _data);

        public bool SequenceEqual(ReadOnlySpan<byte> other)
        {
            var length = Length;
            if (length != other.Length)
            {
                return false;
            }

            ref byte first = ref Unsafe.Add(ref _data, ValueOffset);
            ref byte second = ref MemoryMarshal.GetReference(other);

            int offset = length - Vector128<byte>.Count;

            return
                ((Vector128.LoadUnsafe(ref first) ^ Vector128.LoadUnsafe(ref second)) |
                 (Vector128.LoadUnsafe(ref Unsafe.Add(ref first, offset)) ^
                  Vector128.LoadUnsafe(ref Unsafe.Add(ref second, offset)))) ==
                Vector128<byte>.Zero;
        }

        public static int MemorySize => Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    [InlineArray(Size)]
    private struct Payload32 : IPayload
    {
        private byte _data;
        private const int Size = 32;

        public void SetSize(int size)
        {
            _data = (byte)size;
        }

        public void Load(ReadOnlySpan<byte> src)
        {
            _data = (byte)src.Length;
            src.CopyTo(Span);
        }

        public byte Length => _data;

        public Span<byte> Span => MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _data, ValueOffset), _data);

        public bool SequenceEqual(ReadOnlySpan<byte> other)
        {
            var length = Length;
            if (length != other.Length)
            {
                return false;
            }

            ref byte first = ref Unsafe.Add(ref _data, ValueOffset);
            ref byte second = ref MemoryMarshal.GetReference(other);

            int offset = length - Vector128<byte>.Count;

            return
                ((Vector128.LoadUnsafe(ref first) ^ Vector128.LoadUnsafe(ref second)) |
                 (Vector128.LoadUnsafe(ref Unsafe.Add(ref first, offset)) ^
                  Vector128.LoadUnsafe(ref Unsafe.Add(ref second, offset)))) ==
                Vector128<byte>.Zero;
        }

        public static int MemorySize => Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    [InlineArray(Size)]
    private struct PayloadFixed32 : IPayload
    {
        private const int Size = 32;
        private byte _data;
        private const int Capacity = Size;

        public void SetSize(int size) { }

        public void Load(ReadOnlySpan<byte> src)
        {
            Unsafe.As<byte, Vector256<byte>>(ref _data) = Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(src));
        }

        public byte Length => Size;

        public Span<byte> Span => MemoryMarshal.CreateSpan(ref _data, Capacity);

        public bool SequenceEqual(ReadOnlySpan<byte> other)
        {
            return Length == other.Length && Unsafe.As<byte, Vector256<byte>>(ref _data) == Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(other));
        }

        public static int MemorySize => Size;
    }
}
