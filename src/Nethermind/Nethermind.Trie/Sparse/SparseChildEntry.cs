// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// A single child entry in a branch node's dense child array.
/// Either a revealed child (index into the arena) or a blinded child (hash-only stub).
/// </summary>
public struct SparseChildEntry
{
    private int _arenaIndex;
    private RlpNode _blindedRlp;
    private bool _isRevealed;

    public bool IsRevealed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _isRevealed;
    }

    public bool IsBlinded
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !_isRevealed;
    }

    public int ArenaIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _arenaIndex;
    }

    public RlpNode BlindedRlp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _blindedRlp;
    }

    public static SparseChildEntry Revealed(int arenaIndex) => new()
    {
        _arenaIndex = arenaIndex,
        _isRevealed = true,
    };

    public static SparseChildEntry Blinded(RlpNode rlpNode) => new()
    {
        _blindedRlp = rlpNode,
        _isRevealed = false,
    };

    /// <summary>
    /// Returns the RLP node for this child reference (for use in parent encoding).
    /// For revealed children, the caller must supply the cached RLP from the arena.
    /// For blinded children, returns the stored hash/RLP directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RlpNode GetChildRlp(SparseTrieNode[] arena)
    {
        if (_isRevealed)
            return arena[_arenaIndex].CachedRlp;
        return _blindedRlp;
    }

    public override string ToString() => _isRevealed
        ? $"Revealed(idx={_arenaIndex})"
        : $"Blinded({_blindedRlp.Length}B)";
}
