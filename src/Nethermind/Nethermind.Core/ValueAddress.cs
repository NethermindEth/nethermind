// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core;

// 20-byte unmanaged form of Address. Backs Address itself and is also usable directly
// by callers that want an address-shaped key/value without a managed allocation.
[StructLayout(LayoutKind.Sequential, Size = Address.Size)]
public readonly struct ValueAddress
{
    [InlineArray(Address.Size)]
    private struct Bytes20 { private byte _e0; }

    private readonly Bytes20 _bytes;

    public ValueAddress(ReadOnlySpan<byte> bytes)
    {
        Debug.Assert(bytes.Length == Address.Size);
        bytes.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<Bytes20, byte>(ref Unsafe.AsRef(in _bytes)), Address.Size));
    }

    public ReadOnlySpan<byte> AsSpan
        => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<Bytes20, byte>(ref Unsafe.AsRef(in _bytes)), Address.Size);

    public Address ToAddress() => new(AsSpan);
}
