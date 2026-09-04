// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Precompiles;

/// <summary>
/// Values keyed by precompile address, looked up by <see cref="Address.PrecompileIndexOrNegative"/> instead of by hash.
/// </summary>
/// <remarks>
/// Precompiles sit at low, near-contiguous addresses, so their index doubles as a dense array slot.
/// The array only covers indices up to <see cref="MaxDenseIndex"/>; anything above it - a plugin precompile
/// placed far out, such as Taiko's L1SLOAD - is served by the backing dictionary, so the array stays small.
/// </remarks>
public sealed class PrecompileTable<T> where T : class
{
    /// <summary> Highest index kept in the dense array, sized to hold the standard precompiles (up to P256VERIFY). </summary>
    private const int MaxDenseIndex = 0x100;

    private readonly T?[] _byIndex;
    private readonly FrozenDictionary<AddressAsKey, T> _byAddress;

    public PrecompileTable(FrozenDictionary<AddressAsKey, T> entries)
    {
        _byAddress = entries;

        int maxIndex = -1;
        foreach (KeyValuePair<AddressAsKey, T> entry in entries)
        {
            int index = entry.Key.Value.PrecompileIndexOrNegative();
            if (index > maxIndex && index <= MaxDenseIndex) maxIndex = index;
        }

        _byIndex = maxIndex < 0 ? [] : new T[maxIndex + 1];
        foreach (KeyValuePair<AddressAsKey, T> entry in entries)
        {
            int index = entry.Key.Value.PrecompileIndexOrNegative();
            if ((uint)index < (uint)_byIndex.Length) _byIndex[index] = entry.Value;
        }
    }

    /// <exception cref="KeyNotFoundException"><paramref name="address"/> holds no precompile.</exception>
    public T this[Address address] => TryGetValue(address, out T? value) ? value : _byAddress[address];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(Address address, [NotNullWhen(true)] out T? value)
    {
        T?[] byIndex = _byIndex;
        int index = address.PrecompileIndexOrNegative();
        if ((uint)index < (uint)byIndex.Length)
        {
            value = byIndex[index];
            if (value is not null) return true;
        }

        return _byAddress.TryGetValue(address, out value);
    }
}
