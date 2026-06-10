// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.CodeAnalysis.IlEvm;

/// <summary>
/// The IL-EVM intermediate representation of one bytecode: the code cut into
/// <see cref="BasicBlock"/>s, with a program-counter index for jump-target lookup.
/// Built once per (code, spec) by <see cref="BytecodeAnalyzer"/> and intended to be cached
/// alongside <see cref="CodeInfo"/>, which is already keyed by code hash.
/// </summary>
public sealed class AnalyzedCode
{
    public static readonly AnalyzedCode Empty = new([], []);

    private readonly BasicBlock[] _blocks;
    // Block index for positions that start a block, -1 elsewhere; length equals code length.
    private readonly int[] _blockIndexByPc;

    internal AnalyzedCode(BasicBlock[] blocks, int[] blockIndexByPc)
    {
        _blocks = blocks;
        _blockIndexByPc = blockIndexByPc;
    }

    public ReadOnlySpan<BasicBlock> Blocks => _blocks;

    public int BlockCount => _blocks.Length;

    public int CodeLength => _blockIndexByPc.Length;

    /// <summary>
    /// Looks up the block whose first opcode sits at <paramref name="programCounter"/> —
    /// the query a jump lands on, and the hook the segment dispatcher will use.
    /// </summary>
    public bool TryGetBlockStartingAt(int programCounter, out BasicBlock block)
    {
        if (TryGetBlockIndexStartingAt(programCounter, out int index))
        {
            block = _blocks[index];
            return true;
        }

        block = default;
        return false;
    }

    /// <summary>Index variant for callers that maintain per-block side tables (compiled segments).</summary>
    public bool TryGetBlockIndexStartingAt(int programCounter, out int index)
    {
        if ((uint)programCounter < (uint)_blockIndexByPc.Length)
        {
            index = _blockIndexByPc[programCounter];
            return index >= 0;
        }

        index = -1;
        return false;
    }
}
