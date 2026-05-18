// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Concatenated <c>uint</c> index lane covering an account's balance / nonce / code change
/// families. One allocation backs all three; per-family spans are carved out via the
/// <see cref="Balance"/>, <see cref="Nonce"/>, <see cref="Code"/> accessors. Drives binary-search
/// lookups over a dense 4-byte key per element instead of walking the fat change structs.
/// </summary>
internal readonly struct AccountIndexLane
{
    private readonly uint[] _indices;
    private readonly int _nonceStart;
    private readonly int _codeStart;

    public AccountIndexLane(BalanceChange[] balanceChanges, NonceChange[] nonceChanges, CodeChange[] codeChanges)
    {
        int balance = balanceChanges.Length;
        int nonce = nonceChanges.Length;
        int code = codeChanges.Length;
        int total = balance + nonce + code;
        if (total == 0)
        {
            _indices = [];
            _nonceStart = 0;
            _codeStart = 0;
            return;
        }

        uint[] indices = new uint[total];
        for (int i = 0; i < balance; i++) indices[i] = balanceChanges[i].Index;
        int nonceStart = balance;
        for (int i = 0; i < nonce; i++) indices[nonceStart + i] = nonceChanges[i].Index;
        int codeStart = nonceStart + nonce;
        for (int i = 0; i < code; i++) indices[codeStart + i] = codeChanges[i].Index;

        _indices = indices;
        _nonceStart = nonceStart;
        _codeStart = codeStart;
    }

    public ReadOnlySpan<uint> Balance => _indices.AsSpan(0, _nonceStart);
    public ReadOnlySpan<uint> Nonce => _indices.AsSpan(_nonceStart, _codeStart - _nonceStart);
    public ReadOnlySpan<uint> Code => _indices.AsSpan(_codeStart);

    /// <summary>Entry from <paramref name="values"/> at exactly <c>Index == index</c>, or null.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static T? GetExact<T>(ReadOnlySpan<uint> indices, T[] values, uint index) where T : struct
    {
        int idx = indices.BinarySearch(index);
        return idx >= 0 ? values[idx] : null;
    }

    /// <summary>Entry with the largest Index strictly less than <paramref name="blockAccessIndex"/>;
    /// returns <c>false</c> if none.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool TryGetLastBefore<T>(ReadOnlySpan<uint> indices, T[] values, uint blockAccessIndex, out T last) where T : struct
    {
        int idx = indices.BinarySearch(blockAccessIndex);
        // idx (if found) or ~idx (if not) is the position of the first entry with Index >= target;
        // strictly-before is one step earlier.
        int lastBefore = (idx >= 0 ? idx : ~idx) - 1;
        if (lastBefore < 0)
        {
            last = default;
            return false;
        }
        last = values[lastBefore];
        return true;
    }
}
