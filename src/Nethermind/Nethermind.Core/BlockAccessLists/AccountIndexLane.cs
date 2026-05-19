// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Concatenated <c>uint</c> index lane covering an account's balance / nonce / code change
/// families. One allocation backs all three; the lane exposes per-family lookup helpers that
/// binary-search the dense 4-byte key array, avoiding a walk over the fat change structs.
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

    private ReadOnlySpan<uint> Balances => _indices.AsSpan(0, _nonceStart);
    private ReadOnlySpan<uint> Nonces => _indices.AsSpan(_nonceStart, _codeStart - _nonceStart);
    private ReadOnlySpan<uint> Codes => _indices.AsSpan(_codeStart);

    public BalanceChange? GetExact(BalanceChange[] values, uint index) => IndexLane.GetExact(Balances, values, index);
    public NonceChange? GetExact(NonceChange[] values, uint index) => IndexLane.GetExact(Nonces, values, index);
    public CodeChange? GetExact(CodeChange[] values, uint index) => IndexLane.GetExact(Codes, values, index);

    public bool TryGetLastBefore(BalanceChange[] values, uint blockAccessIndex, out BalanceChange last)
        => IndexLane.TryGetLastBefore(Balances, values, blockAccessIndex, out last);

    public bool TryGetLastBefore(NonceChange[] values, uint blockAccessIndex, out NonceChange last)
        => IndexLane.TryGetLastBefore(Nonces, values, blockAccessIndex, out last);

    public bool TryGetLastBefore(CodeChange[] values, uint blockAccessIndex, out CodeChange last)
        => IndexLane.TryGetLastBefore(Codes, values, blockAccessIndex, out last);
}
