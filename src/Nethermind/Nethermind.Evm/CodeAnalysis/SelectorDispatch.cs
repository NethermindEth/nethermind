// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Matches the Solidity function dispatcher that opens most contracts: free-memory-pointer setup, an
/// optional contract-wide non-payable guard, the 4-byte calldata-length check, the selector extraction,
/// and the comparison structure that routes the selector to its function body.
/// </summary>
/// <remarks>
/// Both shapes solc emits are matched: a linear chain of equality tests, and the binary search tree it
/// switches to for larger interfaces. A resolved entry gives the function body's program counter and the
/// gas the skipped opcodes would have consumed, so the interpreter can resume at the body with identical
/// gas, stack and memory. Gas is accumulated from the opcodes actually walked along each selector's path
/// rather than from a formula, and the tree's routing is verified against its own pivots, so a match
/// never depends on the compiler having laid the tree out the way it usually does.
/// </remarks>
internal sealed class SelectorDispatch
{
    /// <summary>Free-memory-pointer slot, written with <see cref="InitialFreeMemoryPointer"/> by the preamble.</summary>
    public const int FreeMemoryPointerSlot = 0x40;

    /// <summary>Value the preamble stores at <see cref="FreeMemoryPointerSlot"/>.</summary>
    public const int InitialFreeMemoryPointer = 0x80;

    /// <summary>Memory size after the preamble's MSTORE, in bytes.</summary>
    public const ulong MemorySizeAfterPreamble = FreeMemoryPointerSlot + EvmPooledMemory.WordSize;

    /// <summary>DUP1, PUSH4 operand, the comparison, PUSH destination, JUMPI: one node of either shape.</summary>
    private const ulong ComparisonGas = GasCostOf.VeryLow * 4 + GasCostOf.JumpI;

    /// <summary>Caps analysis of adversarial code; real dispatchers are far below both limits.</summary>
    private const int MaxEntries = 512;

    private const int MaxDepth = 64;

    private readonly uint[] _selectors;
    private readonly int[] _targets;
    private readonly ulong[] _gas;
    private readonly bool[] _guarded;

    private SelectorDispatch(uint[] selectors, int[] targets, ulong[] gas, bool[] guarded, bool rejectsCallValue)
    {
        _selectors = selectors;
        _targets = targets;
        _gas = gas;
        _guarded = guarded;
        RejectsCallValue = rejectsCallValue;
    }

    /// <summary>Whether the preamble carries the contract-wide guard that reverts on a non-zero call value.</summary>
    /// <remarks>
    /// The fast path declines when this holds and the frame carries value, leaving the dispatch loop to
    /// run the revert.
    /// </remarks>
    public bool RejectsCallValue { get; }

    /// <summary>Resolves a selector to the code to resume at and the gas the skipped opcodes cost.</summary>
    /// <param name="selector">The leading four calldata bytes, big-endian.</param>
    /// <param name="hasCallValue">Whether the frame carries a non-zero value.</param>
    /// <param name="programCounter">Where to resume: the function body, past its own non-payable guard when it has one.</param>
    /// <param name="gasCost">Gas consumed by the preamble, every comparison on the selector's path, and any guard skipped.</param>
    /// <returns>
    /// <see langword="false"/> when no entry matches — the code would fall through to its fallback — or when the
    /// matched function's own guard would reject the call's value, which leaves the dispatch loop to run the revert.
    /// </returns>
    public bool TryResolve(uint selector, bool hasCallValue, out int programCounter, out ulong gasCost)
    {
        uint[] selectors = _selectors;
        for (int i = 0; i < selectors.Length; i++)
        {
            if (selectors[i] != selector) continue;
            if (hasCallValue && _guarded[i]) break;

            programCounter = _targets[i];
            gasCost = _gas[i];
            return true;
        }

        programCounter = 0;
        gasCost = 0;
        return false;
    }

    /// <summary>Recognizes the dispatcher at the start of <paramref name="code"/>, or returns <see langword="null"/>.</summary>
    /// <param name="code">The contract's runtime bytecode.</param>
    /// <param name="isValidJumpDestination">Validates a jump target against the jump-destination bitmap.</param>
    public static SelectorDispatch? TryMatch(ReadOnlySpan<byte> code, Func<int, bool> isValidJumpDestination)
    {
        Reader reader = new(code);

        // PUSH1 0x80 PUSH1 0x40 MSTORE: initialize the free memory pointer.
        if (!reader.TryPush1(InitialFreeMemoryPointer) ||
            !reader.TryPush1(FreeMemoryPointerSlot) ||
            !reader.TryOpcode(Instruction.MSTORE)) return null;
        reader.Charge(MemoryExpansionGas(MemorySizeAfterPreamble));

        bool rejectsCallValue = TryMatchCallValueGuard(ref reader, isValidJumpDestination);

        // PUSH1 0x04 CALLDATASIZE LT PUSH<n> fallback JUMPI: not taken for a selector-bearing call.
        if (!reader.TryPush1(4) ||
            !reader.TryOpcode(Instruction.CALLDATASIZE) ||
            !reader.TryOpcode(Instruction.LT) ||
            !reader.TryPushJumpTarget(out _) ||
            !reader.TryOpcode(Instruction.JUMPI)) return null;

        // PUSH1 0x00 CALLDATALOAD PUSH1 0xE0 SHR: shift the selector down to the low four bytes.
        if (!reader.TryPush1(0) ||
            !reader.TryOpcode(Instruction.CALLDATALOAD) ||
            !reader.TryPush1(0xE0) ||
            !reader.TryOpcode(Instruction.SHR)) return null;

        List<Entry> entries = [];
        if (!TryParseNode(code, isValidJumpDestination, entries, reader.Position, reader.Gas, depth: 0) ||
            entries.Count == 0 ||
            HasDuplicateSelectors(entries)) return null;

        uint[] selectors = new uint[entries.Count];
        int[] targets = new int[entries.Count];
        ulong[] gas = new ulong[entries.Count];
        bool[] guarded = new bool[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            selectors[i] = entries[i].Selector;
            targets[i] = entries[i].Target;
            gas[i] = entries[i].Gas;
            guarded[i] = entries[i].Guarded;
        }

        return new SelectorDispatch(selectors, targets, gas, guarded, rejectsCallValue);
    }

    /// <summary>One selector's resolved body, the gas consumed reaching it, and whether it rejects call value.</summary>
    private readonly record struct Entry(uint Selector, int Target, ulong Gas, bool Guarded);

    /// <summary>
    /// Parses one node of the comparison structure: either a pivot test that splits into two subtrees, or
    /// a run of equality tests.
    /// </summary>
    /// <remarks>
    /// A pivot test is only accepted once both of its subtrees have been parsed and every selector in them
    /// is shown to fall on the side the pivot sends it to. That check is what makes the gas recorded along
    /// the parse path equal the gas the EVM charges, without assuming how the compiler ordered the tree.
    /// </remarks>
    private static bool TryParseNode(
        ReadOnlySpan<byte> code,
        Func<int, bool> isValidJumpDestination,
        List<Entry> entries,
        int position,
        ulong gas,
        int depth)
    {
        if (depth > MaxDepth || entries.Count > MaxEntries) return false;

        Reader reader = new(code) { Position = position };
        if (TryReadComparison(ref reader, out uint pivot, out Instruction comparison, out int branch) &&
            comparison is Instruction.GT or Instruction.LT)
        {
            if (!isValidJumpDestination(branch) || !IsJumpDestination(code, branch)) return false;

            // The taken branch lands on a JUMPDEST, which the EVM charges before the subtree runs.
            int takenFirst = entries.Count;
            if (!TryParseNode(code, isValidJumpDestination, entries, branch + 1,
                    gas + ComparisonGas + GasCostOf.JumpDest, depth + 1)) return false;

            int fallThroughFirst = entries.Count;
            if (!TryParseNode(code, isValidJumpDestination, entries, reader.Position,
                    gas + ComparisonGas, depth + 1)) return false;

            // GT computes "pivot > selector", LT computes "pivot < selector"; the taken branch is the side
            // that comparison selects, and the fall-through takes everything else.
            bool takenIsBelowPivot = comparison == Instruction.GT;
            return AllSelectorsSatisfy(entries, takenFirst, fallThroughFirst, pivot, below: takenIsBelowPivot) &&
                   AllSelectorsSatisfy(entries, fallThroughFirst, entries.Count, pivot, below: !takenIsBelowPivot);
        }

        return TryParseEqualityRun(code, isValidJumpDestination, entries, position, gas);
    }

    /// <summary>Parses consecutive equality tests, each sending its selector to a function body.</summary>
    private static bool TryParseEqualityRun(
        ReadOnlySpan<byte> code,
        Func<int, bool> isValidJumpDestination,
        List<Entry> entries,
        int position,
        ulong gas)
    {
        Reader reader = new(code) { Position = position };
        int matched = 0;

        while (entries.Count <= MaxEntries &&
               TryReadComparison(ref reader, out uint selector, out Instruction comparison, out int target) &&
               comparison == Instruction.EQ)
        {
            if (!isValidJumpDestination(target)) return false;

            ulong bodyGas = gas + ComparisonGas;
            bool guarded = TrySkipFunctionCallValueGuard(code, isValidJumpDestination, ref target, ref bodyGas);
            entries.Add(new Entry(selector, target, bodyGas, guarded));
            matched++;
            gas += ComparisonGas;
        }

        return matched > 0;
    }

    /// <summary>
    /// Consumes a function's own non-payable guard when one opens its body, advancing past it.
    /// </summary>
    /// <remarks>
    /// solc emits this per function instead of contract-wide whenever some other function is payable.
    /// The guard reverts a value-bearing call, so an entry carrying one is only resolved for frames with
    /// no value; on that path the guard's opcodes all run and are charged here. The resume point is past
    /// the trailing POP that drops the duplicated call value, leaving the body's expected stack.
    /// </remarks>
    private static bool TrySkipFunctionCallValueGuard(
        ReadOnlySpan<byte> code,
        Func<int, bool> isValidJumpDestination,
        ref int target,
        ref ulong gas)
    {
        Reader reader = new(code) { Position = target };

        // JUMPDEST CALLVALUE DUP1 ISZERO PUSH<n> body JUMPI PUSH1 0x00 DUP1 REVERT
        if (!reader.TryOpcode(Instruction.JUMPDEST) ||
            !reader.TryOpcode(Instruction.CALLVALUE) ||
            !reader.TryOpcode(Instruction.DUP1) ||
            !reader.TryOpcode(Instruction.ISZERO) ||
            !reader.TryPushJumpTarget(out int body) ||
            !reader.TryOpcode(Instruction.JUMPI)) return false;

        // The revert arm is jumped over, so its opcodes are skipped rather than charged.
        if (!reader.TrySkipPush1(0) ||
            !reader.TrySkipOpcode(Instruction.DUP1) ||
            !reader.TrySkipOpcode(Instruction.REVERT)) return false;

        if (body != reader.Position || !isValidJumpDestination(body)) return false;

        if (!reader.TryOpcode(Instruction.JUMPDEST) ||
            !reader.TryOpcode(Instruction.POP)) return false;

        target = reader.Position;
        gas += reader.Gas;
        return true;
    }

    /// <summary>Reads a DUP1 PUSH4 &lt;operand&gt; &lt;comparison&gt; PUSH&lt;n&gt; &lt;target&gt; JUMPI node.</summary>
    private static bool TryReadComparison(ref Reader reader, out uint operand, out Instruction comparison, out int target)
    {
        operand = 0;
        comparison = default;
        target = 0;

        Reader probe = reader;
        if (!probe.TrySkipOpcode(Instruction.DUP1) || !probe.TryReadPush4(out operand)) return false;
        if (!probe.TryReadOpcode(out comparison)) return false;
        if (!probe.TrySkipPushJumpTarget(out target) || !probe.TrySkipOpcode(Instruction.JUMPI)) return false;

        reader = probe;
        return true;
    }

    /// <summary>Checks that every selector recorded in a subtree falls on the side the pivot routes it to.</summary>
    private static bool AllSelectorsSatisfy(List<Entry> entries, int first, int last, uint pivot, bool below)
    {
        for (int i = first; i < last; i++)
        {
            if (below ? entries[i].Selector >= pivot : entries[i].Selector < pivot) return false;
        }

        return true;
    }

    private static bool HasDuplicateSelectors(List<Entry> entries)
    {
        HashSet<uint> seen = new(entries.Count);
        foreach (Entry entry in entries)
        {
            if (!seen.Add(entry.Selector)) return true;
        }

        return false;
    }

    private static bool IsJumpDestination(ReadOnlySpan<byte> code, int position) =>
        (uint)position < (uint)code.Length && code[position] == (byte)Instruction.JUMPDEST;

    /// <summary>
    /// Consumes the contract-wide non-payable guard when present, leaving the reader untouched otherwise.
    /// </summary>
    private static bool TryMatchCallValueGuard(ref Reader reader, Func<int, bool> isValidJumpDestination)
    {
        Reader probe = reader;

        // CALLVALUE DUP1 ISZERO PUSH<n> body JUMPI PUSH1 0x00 DUP1 REVERT
        if (!probe.TryOpcode(Instruction.CALLVALUE) ||
            !probe.TryOpcode(Instruction.DUP1) ||
            !probe.TryOpcode(Instruction.ISZERO) ||
            !probe.TryPushJumpTarget(out int body) ||
            !probe.TryOpcode(Instruction.JUMPI)) return false;

        // The revert arm is jumped over, so its opcodes are skipped rather than charged.
        if (!probe.TrySkipPush1(0) ||
            !probe.TrySkipOpcode(Instruction.DUP1) ||
            !probe.TrySkipOpcode(Instruction.REVERT)) return false;

        // The guard is only recognized when the taken branch lands on the JUMPDEST that follows it.
        if (body != probe.Position || !isValidJumpDestination(body)) return false;

        if (!probe.TryOpcode(Instruction.JUMPDEST) ||
            !probe.TryOpcode(Instruction.POP)) return false;

        reader = probe;
        return true;
    }

    /// <summary>Memory expansion cost for a first growth to <paramref name="size"/> bytes, per the Yellow Paper's memory cost function.</summary>
    private static ulong MemoryExpansionGas(ulong size)
    {
        ulong words = (size + EvmPooledMemory.WordSize - 1) / EvmPooledMemory.WordSize;
        return GasCostOf.Memory * words + words * words / 512;
    }

    /// <summary>Walks the opcode sequence, accumulating the gas the matched opcodes consume.</summary>
    private ref struct Reader(ReadOnlySpan<byte> code)
    {
        private readonly ReadOnlySpan<byte> _code = code;

        public int Position { get; set; }

        public ulong Gas { get; private set; }

        public void Charge(ulong gas) => Gas += gas;

        public bool TryOpcode(Instruction instruction)
        {
            if (!TrySkipOpcode(instruction)) return false;

            Gas += StaticGasOf(instruction);
            return true;
        }

        public bool TrySkipOpcode(Instruction instruction)
        {
            if (Position >= _code.Length || _code[Position] != (byte)instruction) return false;

            Position++;
            return true;
        }

        public bool TryReadOpcode(out Instruction instruction)
        {
            if (Position >= _code.Length)
            {
                instruction = default;
                return false;
            }

            instruction = (Instruction)_code[Position];
            Position++;
            return true;
        }

        public bool TryPush1(int value)
        {
            if (!TrySkipPush1(value)) return false;

            Gas += GasCostOf.VeryLow;
            return true;
        }

        public bool TrySkipPush1(int value)
        {
            if (Position + 1 >= _code.Length ||
                _code[Position] != (byte)Instruction.PUSH1 ||
                _code[Position + 1] != (byte)value) return false;

            Position += 2;
            return true;
        }

        public bool TryReadPush4(out uint value)
        {
            value = 0;
            if (Position + 4 >= _code.Length || _code[Position] != (byte)Instruction.PUSH4) return false;

            value = (uint)((_code[Position + 1] << 24) |
                           (_code[Position + 2] << 16) |
                           (_code[Position + 3] << 8) |
                           _code[Position + 4]);
            Position += 5;
            return true;
        }

        /// <summary>Reads a PUSH1 or PUSH2 carrying a jump target.</summary>
        public bool TryPushJumpTarget(out int target)
        {
            if (!TrySkipPushJumpTarget(out target)) return false;

            Gas += GasCostOf.VeryLow;
            return true;
        }

        /// <summary>Reads a PUSH1 or PUSH2 carrying a jump target without charging for it.</summary>
        public bool TrySkipPushJumpTarget(out int target)
        {
            target = 0;
            if (Position >= _code.Length) return false;

            int width = _code[Position] switch
            {
                (byte)Instruction.PUSH1 => 1,
                (byte)Instruction.PUSH2 => 2,
                _ => 0,
            };
            if (width == 0 || Position + width >= _code.Length) return false;

            for (int i = 1; i <= width; i++)
            {
                target = (target << 8) | _code[Position + i];
            }

            Position += width + 1;
            return true;
        }

        /// <summary>Static gas for the opcodes this grammar accepts.</summary>
        private static ulong StaticGasOf(Instruction instruction) => instruction switch
        {
            Instruction.CALLVALUE or Instruction.CALLDATASIZE or Instruction.POP => GasCostOf.Base,
            Instruction.JUMPDEST => GasCostOf.JumpDest,
            Instruction.JUMPI => GasCostOf.JumpI,
            Instruction.MSTORE or Instruction.DUP1 or Instruction.ISZERO
                or Instruction.LT or Instruction.EQ or Instruction.CALLDATALOAD
                or Instruction.SHR => GasCostOf.VeryLow,
            _ => throw new ArgumentOutOfRangeException(nameof(instruction), instruction, "Opcode is outside the dispatcher grammar."),
        };
    }
}
