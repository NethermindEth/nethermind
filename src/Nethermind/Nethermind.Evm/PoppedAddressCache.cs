// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>
/// Single-entry cache that reuses the most recently materialized <see cref="Address"/> popped from the EVM stack.
/// Contracts frequently address the same account repeatedly (token transfer loops, repeated BALANCE/EXTCODE*
/// checks, delegate targets), so reusing the previous instance removes one heap allocation per
/// address-consuming opcode. A miss costs a 20-byte comparison before falling back to allocation.
/// Not thread-safe; owned by a single <see cref="VirtualMachine{TGasPolicy}"/> instance.
/// </summary>
public sealed class PoppedAddressCache
{
    private Address? _lastAddress;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Address GetOrCreate(ReadOnlySpan<byte> addressBytes)
    {
        Address? last = _lastAddress;
        if (last is not null && addressBytes.SequenceEqual(last.Bytes))
        {
            return last;
        }

        Address address = new(addressBytes);
        _lastAddress = address;
        return address;
    }
}
