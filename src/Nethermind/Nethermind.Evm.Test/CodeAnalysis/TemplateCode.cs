// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Evm.Test.CodeAnalysis;

/// <summary>How a dispatcher routes a selector to its function body.</summary>
public enum DispatchShape
{
    /// <summary>A run of equality tests, as solc emits for a small interface.</summary>
    Linear,

    /// <summary>A binary search over pivots, as solc emits once the interface grows.</summary>
    BinarySearch,
}

/// <summary>Assembles the exact opcode sequences the templates recognize, for tests to match against.</summary>
public static class TemplateCode
{
    /// <summary>A recognized dispatcher together with the offsets a test needs to assert against.</summary>
    public readonly record struct Dispatcher(byte[] Code, IReadOnlyDictionary<uint, int> Bodies, int FallbackProgramCounter);

    /// <summary>Leaves of this size or smaller become a run of equality tests rather than splitting again.</summary>
    private const int MaxLeafSize = 2;

    public static byte[] MinimalProxy(Address target) =>
    [
        0x36, 0x3d, 0x3d, 0x37, 0x3d, 0x3d, 0x3d, 0x36, 0x3d, 0x73,
        .. target.Bytes,
        0x5a, 0xf4, 0x3d, 0x82, 0x80, 0x3e, 0x90, 0x3d, 0x91, 0x60, 0x2b, 0x57, 0xfd, 0x5b, 0xf3,
    ];

    /// <summary>
    /// The 44-byte "0age" forwarder: four RETURNDATASIZE, CALLDATASIZE RETURNDATASIZE RETURNDATASIZE
    /// CALLDATACOPY, CALLDATASIZE RETURNDATASIZE PUSH20 target GAS DELEGATECALL, then RETURNDATASIZE
    /// RETURNDATASIZE SWAP4 DUP1 RETURNDATACOPY PUSH1 0x2a JUMPI REVERT JUMPDEST RETURN.
    /// </summary>
    public static byte[] AgeMinimalProxy(Address target) =>
    [
        0x3d, 0x3d, 0x3d, 0x3d, 0x36, 0x3d, 0x3d, 0x37, 0x36, 0x3d, 0x73,
        .. target.Bytes,
        0x5a, 0xf4, 0x3d, 0x3d, 0x93, 0x80, 0x3e, 0x60, 0x2a, 0x57, 0xfd, 0x5b, 0xf3,
    ];

    /// <summary>ERC-7511's 44-byte PUSH0 forwarder.</summary>
    public static byte[] Erc7511MinimalProxy(Address target) =>
    [
        0x36, 0x5f, 0x5f, 0x37, 0x5f, 0x5f, 0x36, 0x5f, 0x73,
        .. target.Bytes,
        0x5a, 0xf4, 0x3d, 0x5f, 0x5f, 0x3e, 0x5f, 0x3d, 0x91, 0x60, 0x2a, 0x57, 0xfd, 0x5b, 0xf3,
    ];

    /// <summary>Solady's 45-byte PUSH0 forwarder, whose return and revert tails both rebuild their operands.</summary>
    public static byte[] SoladyMinimalProxy(Address target) =>
    [
        0x5f, 0x5f, 0x36, 0x5f, 0x5f, 0x37, 0x36, 0x5f, 0x73,
        .. target.Bytes,
        0x5a, 0xf4, 0x3d, 0x5f, 0x5f, 0x3e, 0x60, 0x29, 0x57, 0x3d, 0x5f, 0xfd, 0x5b, 0x3d, 0x5f, 0xf3,
    ];

    /// <summary>
    /// A minimal proxy that behaves identically and costs identical gas, but is not recognized, so a
    /// benchmark can compare the fast path against the dispatch loop without a runtime switch.
    /// </summary>
    /// <remarks>
    /// The trailing <c>PUSH1 0x2b</c> becomes <c>PUSH2 0x002c</c>: same price, same pushed value once the
    /// extra byte shifts the JUMPDEST, but a length the 45-byte template match rejects.
    /// </remarks>
    public static byte[] UnrecognizedMinimalProxy(Address target) =>
    [
        0x36, 0x3d, 0x3d, 0x37, 0x3d, 0x3d, 0x3d, 0x36, 0x3d, 0x73,
        .. target.Bytes,
        0x5a, 0xf4, 0x3d, 0x82, 0x80, 0x3e, 0x90, 0x3d, 0x91, 0x61, 0x00, 0x2c, 0x57, 0xfd, 0x5b, 0xf3,
    ];

    /// <summary>
    /// Builds the Solidity preamble, the requested routing shape over <paramref name="selectors"/>, a
    /// reverting fallback, and a <c>JUMPDEST STOP</c> body per selector.
    /// </summary>
    /// <param name="perFunctionCallValueGuard">
    /// When <see langword="true"/>, each body opens with its own non-payable guard, as solc emits when
    /// some other function in the contract is payable.
    /// </param>
    /// <param name="recognized">
    /// When <see langword="false"/>, the leading <c>PUSH1 0x80</c> is emitted as <c>PUSH2 0x0080</c>:
    /// identical behaviour and gas, but a preamble the template match rejects, which lets a benchmark
    /// compare the fast path against the dispatch loop without a runtime switch.
    /// </param>
    public static Dispatcher SelectorDispatch(
        uint[] selectors,
        bool withCallValueGuard,
        DispatchShape shape = DispatchShape.Linear,
        bool recognized = true,
        bool perFunctionCallValueGuard = false,
        bool push0 = false)
    {
        // Solc targeting Shanghai or later pushes its zeroes with PUSH0 instead of PUSH1 0x00.
        byte[] zero = push0 ? [(byte)Instruction.PUSH0] : [(byte)Instruction.PUSH1, 0x00];

        List<byte> code = recognized
            ? [(byte)Instruction.PUSH1, 0x80, (byte)Instruction.PUSH1, 0x40, (byte)Instruction.MSTORE]
            : [(byte)Instruction.PUSH2, 0x00, 0x80, (byte)Instruction.PUSH1, 0x40, (byte)Instruction.MSTORE];

        if (withCallValueGuard)
        {
            int guardTarget = code.Count + 9 + zero.Length;
            code.AddRange([(byte)Instruction.CALLVALUE, (byte)Instruction.DUP1, (byte)Instruction.ISZERO]);
            code.AddRange(Push2(guardTarget));
            code.Add((byte)Instruction.JUMPI);
            code.AddRange(zero);
            code.AddRange([(byte)Instruction.DUP1, (byte)Instruction.REVERT]);
            code.AddRange([(byte)Instruction.JUMPDEST, (byte)Instruction.POP]);
        }

        // The fallback and the bodies are appended after the routing, so their offsets are patched in below.
        List<int> fallbackPatches = [];
        code.AddRange([(byte)Instruction.PUSH1, 0x04, (byte)Instruction.CALLDATASIZE, (byte)Instruction.LT]);
        fallbackPatches.Add(code.Count + 1);
        code.AddRange(Push2(0));
        code.Add((byte)Instruction.JUMPI);

        code.AddRange(zero);
        code.AddRange([(byte)Instruction.CALLDATALOAD, (byte)Instruction.PUSH1, 0xE0, (byte)Instruction.SHR]);

        Dictionary<uint, int> bodyPatches = [];
        uint[] ordered = [.. selectors];
        if (shape == DispatchShape.BinarySearch)
        {
            Array.Sort(ordered);
            EmitBinarySearch(code, ordered, 0, ordered.Length, bodyPatches, fallbackPatches);
        }
        else
        {
            EmitEqualityRun(code, ordered, 0, ordered.Length, bodyPatches);
        }

        int fallback = code.Count;
        code.Add((byte)Instruction.JUMPDEST);
        code.AddRange(zero);
        code.AddRange([(byte)Instruction.DUP1, (byte)Instruction.REVERT]);
        foreach (int patch in fallbackPatches)
        {
            Patch(code, patch, fallback);
        }

        Dictionary<uint, int> bodies = [];
        foreach (uint selector in ordered)
        {
            bodies[selector] = code.Count;
            Patch(code, bodyPatches[selector], code.Count);
            code.Add((byte)Instruction.JUMPDEST);

            if (perFunctionCallValueGuard)
            {
                // CALLVALUE DUP1 ISZERO PUSH2 body JUMPI PUSH1 0x00 DUP1 REVERT JUMPDEST POP
                code.AddRange([(byte)Instruction.CALLVALUE, (byte)Instruction.DUP1, (byte)Instruction.ISZERO]);
                code.AddRange(Push2(code.Count + 6 + zero.Length));
                code.Add((byte)Instruction.JUMPI);
                code.AddRange(zero);
                code.AddRange([(byte)Instruction.DUP1, (byte)Instruction.REVERT]);
                code.AddRange([(byte)Instruction.JUMPDEST, (byte)Instruction.POP]);
            }

            code.Add((byte)Instruction.STOP);
        }

        return new Dispatcher([.. code], bodies, fallback);
    }

    /// <summary>
    /// Emits <c>DUP1 PUSH4 pivot GT PUSH2 lower JUMPI</c> nodes, with selectors at or above the pivot
    /// falling through and those below reached through the jump — the layout solc produces.
    /// </summary>
    private static void EmitBinarySearch(
        List<byte> code,
        uint[] selectors,
        int low,
        int high,
        Dictionary<uint, int> bodyPatches,
        List<int> fallbackPatches)
    {
        if (high - low <= MaxLeafSize)
        {
            EmitEqualityRun(code, selectors, low, high, bodyPatches);

            // A leaf that matches nothing falls through to the fallback.
            fallbackPatches.Add(code.Count + 1);
            code.AddRange(Push2(0));
            code.Add((byte)Instruction.JUMP);
            return;
        }

        int middle = (low + high) / 2;
        uint pivot = selectors[middle];

        code.AddRange([(byte)Instruction.DUP1, (byte)Instruction.PUSH4]);
        code.AddRange(SelectorBytes(pivot));
        code.Add((byte)Instruction.GT);
        int branchPatch = code.Count + 1;
        code.AddRange(Push2(0));
        code.Add((byte)Instruction.JUMPI);

        EmitBinarySearch(code, selectors, middle, high, bodyPatches, fallbackPatches);

        Patch(code, branchPatch, code.Count);
        code.Add((byte)Instruction.JUMPDEST);
        EmitBinarySearch(code, selectors, low, middle, bodyPatches, fallbackPatches);
    }

    private static void EmitEqualityRun(List<byte> code, uint[] selectors, int low, int high, Dictionary<uint, int> bodyPatches)
    {
        for (int i = low; i < high; i++)
        {
            code.AddRange([(byte)Instruction.DUP1, (byte)Instruction.PUSH4]);
            code.AddRange(SelectorBytes(selectors[i]));
            code.Add((byte)Instruction.EQ);
            bodyPatches[selectors[i]] = code.Count + 1;
            code.AddRange(Push2(0));
            code.Add((byte)Instruction.JUMPI);
        }
    }

    private static byte[] SelectorBytes(uint selector) =>
        [(byte)(selector >> 24), (byte)(selector >> 16), (byte)(selector >> 8), (byte)selector];

    private static byte[] Push2(int value) => [(byte)Instruction.PUSH2, (byte)(value >> 8), (byte)value];

    private static void Patch(List<byte> code, int offset, int value)
    {
        code[offset] = (byte)(value >> 8);
        code[offset + 1] = (byte)value;
    }
}
