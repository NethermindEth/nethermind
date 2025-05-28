// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Unsafe = System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Core;

/// <summary>
/// Non-generic factory entry point for creating tiny arrays with inline payloads.
/// </summary>
public static class TinyArray
{
    /// <summary>
    /// Create a tiny array of minimal inline capacity based on source length.
    /// </summary>
    public static ITinyArray Create(ReadOnlySpan<byte> src)
    {
        int len = src.Length;
        if (len <= Payload8.Capacity) return new TinyArrayImpl<Payload8>(src);
        if (len <= Payload16.Capacity) return new TinyArrayImpl<Payload16>(src);
        if (len <= Payload24.Capacity) return new TinyArrayImpl<Payload24>(src);
        if (len <= Payload32.Capacity) return new TinyArrayImpl<Payload32>(src);

        Debug.Assert(len == PayloadFixed32.Capacity);
        return new TinyArrayImpl<PayloadFixed32>(src);
    }

    private sealed class TinyArrayImpl<TPayload> : ITinyArray
        where TPayload : struct, IPayload
    {
        private TPayload _payload;

        /// <summary>
        /// Construct from a source span. Throws if length > payload capacity.
        /// </summary>
        public TinyArrayImpl(ReadOnlySpan<byte> src)
        {
            _payload = default;
            _payload.Load(src);
        }

        public int Length => _payload.Length;

        public bool SequenceEqual(ReadOnlySpan<byte> other)
            => _payload.SequenceEqual(other);

        public int CommonPrefixLength(ReadOnlySpan<byte> other)
            => _payload.Span.CommonPrefixLength(other);

        public Span<byte> Span => _payload.Span;
    }

    /// <summary>
    /// Payload interface: struct providing inline storage and sequence ops.
    /// </summary>
    private interface IPayload
    {
        void Load(ReadOnlySpan<byte> src);
        byte Length { get; }
        Span<byte> Span { get; }
        bool SequenceEqual(ReadOnlySpan<byte> other);
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
        public const int Capacity = Size - 1;

        public void Load(ReadOnlySpan<byte> src)
        {
            _data = (byte)src.Length;
            src.CopyTo(Span);
        }

        public byte Length => _data;

        public Span<byte> Span => MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _data, ValueOffset), _data);

        public bool SequenceEqual(ReadOnlySpan<byte> other)
        {
            if (Length != other.Length)
                return false;

            return Span.SequenceEqual(other);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    [InlineArray(Size)]
    private struct Payload16 : IPayload
    {
        private byte _data;
        private const int Size = 16;
        public const int Capacity = Size - 1;

        public void Load(ReadOnlySpan<byte> src)
        {
            _data = (byte)src.Length;
            src.CopyTo(Span);
        }

        public byte Length => _data;

        public Span<byte> Span => MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _data, ValueOffset), _data);

        public bool SequenceEqual(ReadOnlySpan<byte> other)
        {
            if (Length != other.Length)
                return false;

            return Span.SequenceEqual(other);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    [InlineArray(Size)]
    private struct Payload24 : IPayload
    {
        private byte _data;
        private const int Size = 24;
        public const int Capacity = Size - 1;

        public void Load(ReadOnlySpan<byte> src)
        {
            _data = (byte)src.Length;
            src.CopyTo(Span);
        }

        public byte Length => _data;

        public Span<byte> Span => MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _data, ValueOffset), _data);

        public bool SequenceEqual(ReadOnlySpan<byte> other)
        {
            if (Length != other.Length)
                return false;

            return Span.SequenceEqual(other);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    [InlineArray(Size)]
    private struct Payload32 : IPayload
    {
        private byte _data;
        private const int Size = 32;
        public const int Capacity = Size - 1;

        public void Load(ReadOnlySpan<byte> src)
        {
            _data = (byte)src.Length;
            src.CopyTo(Span);
        }

        public byte Length => _data;

        public Span<byte> Span => MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _data, ValueOffset), _data);

        public bool SequenceEqual(ReadOnlySpan<byte> other)
        {
            if (Length != other.Length)
                return false;

            return Span.SequenceEqual(other);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    [InlineArray(Size)]
    private struct PayloadFixed32 : IPayload
    {
        private const int Size = 32;
        private byte _data;
        public const int Capacity = Size;

        public void Load(ReadOnlySpan<byte> src)
        {
            Unsafe.As<byte, Vector256<byte>>(ref _data) =
                Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(src));
        }

        public byte Length => Size;

        public Span<byte> Span => MemoryMarshal.CreateSpan(ref _data, Capacity);

        public bool SequenceEqual(ReadOnlySpan<byte> other)
        {
            return Length == other.Length &&
                   Unsafe.As<byte, Vector256<byte>>(ref _data) ==
                   Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(other));
        }
    }
}

/// <summary>
/// A tiny array, allocating just enough space to hold the payload with a byte-long length.
/// </summary>
public interface ITinyArray
{
    int Length { get; }

    bool SequenceEqual(ReadOnlySpan<byte> other);

    int CommonPrefixLength(ReadOnlySpan<byte> other);

    Span<byte> Span { get; }
}
