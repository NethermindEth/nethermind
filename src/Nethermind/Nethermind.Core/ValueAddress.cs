// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core;

/// <summary>
/// 20-byte unmanaged representation of an Ethereum address. Usable as a value-typed
/// key/field without a managed allocation; also backs <see cref="Address"/> internally.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = Address.Size)]
public readonly struct ValueAddress
{
    [InlineArray(Address.Size)]
    private struct Bytes20 { private byte _e0; }

    private readonly Bytes20 _bytes;

    /// <summary>Initializes the address from an exactly-<see cref="Address.Size"/>-byte span.</summary>
    /// <param name="bytes">Source bytes. Must be <see cref="Address.Size"/> bytes long.</param>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not <see cref="Address.Size"/> bytes long.</exception>
    public ValueAddress(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Address.Size)
            throw new ArgumentException($"{nameof(ValueAddress)} must be exactly {Address.Size} bytes, got {bytes.Length}.", nameof(bytes));
        bytes.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<Bytes20, byte>(ref Unsafe.AsRef(in _bytes)), Address.Size));
    }

    /// <summary>Exposes the 20 backing bytes as a read-only span over the struct's storage.</summary>
    // Hot: hit on every Address.Equals/Bytes access. Without force-inline the JIT keeps it out-of-line,
    // blocking the FirstByte/Bytes getters from folding into a direct ref computation.
    public ReadOnlySpan<byte> AsSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<Bytes20, byte>(ref Unsafe.AsRef(in _bytes)), Address.Size);
    }

    /// <summary>Materializes a managed <see cref="Address"/> from this value-typed address.</summary>
    public Address ToAddress() => new(AsSpan);
}
