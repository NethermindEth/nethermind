// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Finds the opcode run that hashes Solidity's 64-byte scratch space — the shape behind every mapping
/// slot computation — so the interpreter can run it as one step instead of six.
/// </summary>
/// <remarks>
/// <para>
/// The run is <c>MSTORE, PUSH1 0x20, MSTORE, PUSH1 0x40, &lt;zero push&gt;, KECCAK256</c>. Only the first
/// MSTORE's offset comes off the stack; every other operand is a constant in the code, which makes the
/// window self-contained: what precedes it never has to be known, so no assumption is made about how the
/// compiler arranged the surrounding stack.
/// </para>
/// <para>
/// Sites are recorded as a bitmap over the leading MSTORE's offset. Two bitmaps are kept because the
/// trailing zero may be pushed with PUSH0, which exists only from Shanghai: fusing that form earlier would
/// skip an opcode the dispatch loop must halt on.
/// </para>
/// </remarks>
internal sealed partial class MappingSlotFusion
{
    /// <summary>Window length when the trailing zero is PUSH0.</summary>
    private const int Push0WindowLength = 8;

    /// <summary>Window length when the trailing zero is PUSH1 0x00.</summary>
    private const int LegacyWindowLength = 9;

    /// <summary>Bytes the scratch hash covers, and so the words KECCAK256 is charged for.</summary>
    public const int ScratchSize = 0x40;

    /// <summary>Offset the second MSTORE writes to.</summary>
    public const int SecondWordOffset = 0x20;

    private const int BitShiftPerInt64 = 6;

    private readonly long[] _legacySites;
    private readonly long[] _allSites;

    private MappingSlotFusion(long[] legacySites, long[] allSites)
    {
        _legacySites = legacySites;
        _allSites = allSites;
    }

    /// <summary>The sites usable under <paramref name="push0Enabled"/>, or <see langword="null"/> when there are none.</summary>
    public long[]? SitesFor(bool push0Enabled)
    {
        long[] sites = push0Enabled ? _allSites : _legacySites;
        return sites.Length == 0 ? null : sites;
    }

    /// <summary>Whether a fusable run starts at the MSTORE at <paramref name="position"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSite(long[] sites, nint position)
    {
        nint index = position >> BitShiftPerInt64;
        return (nuint)index < (nuint)sites.Length &&
               (sites[index] & (1L << (int)(position & 63))) != 0;
    }

    /// <summary>Scans <paramref name="code"/> for fusable runs, or returns <see langword="null"/> when it holds none.</summary>
    /// <remarks>
    /// The scan decodes opcodes so a run is only recorded at a real instruction boundary; bytes that are
    /// PUSH immediates are stepped over rather than matched.
    /// </remarks>
    public static MappingSlotFusion? Find(ReadOnlySpan<byte> code)
    {
        long[]? legacySites = null;
        long[]? allSites = null;
        bool anyPush0 = false;

        for (int position = 0; position < code.Length;)
        {
            byte opcode = code[position];
            if (opcode == (byte)Instruction.MSTORE && TryMatchWindow(code, position, out bool usesPush0))
            {
                allSites ??= NewBitmap(code.Length);
                Set(allSites, position);

                if (usesPush0)
                {
                    anyPush0 = true;
                }
                else
                {
                    legacySites ??= NewBitmap(code.Length);
                    Set(legacySites, position);
                }
            }

            position += OpcodeLength(opcode);
        }

        if (allSites is null) return null;

        // With no PUSH0 window present the two views are identical, so they share one bitmap.
        return new MappingSlotFusion(anyPush0 ? legacySites ?? [] : allSites, allSites);
    }

    /// <summary>Matches the run at <paramref name="position"/>, reporting which form pushes its trailing zero.</summary>
    private static bool TryMatchWindow(ReadOnlySpan<byte> code, int position, out bool usesPush0)
    {
        usesPush0 = false;

        // MSTORE, PUSH1 0x20, MSTORE, PUSH1 0x40 — everything up to the trailing zero push is fixed.
        if (position + Push0WindowLength > code.Length ||
            code[position + 1] != (byte)Instruction.PUSH1 || code[position + 2] != SecondWordOffset ||
            code[position + 3] != (byte)Instruction.MSTORE ||
            code[position + 4] != (byte)Instruction.PUSH1 || code[position + 5] != ScratchSize) return false;

        if (code[position + 6] == (byte)Instruction.PUSH0)
        {
            usesPush0 = true;
            return code[position + 7] == (byte)Instruction.KECCAK256;
        }

        return position + LegacyWindowLength <= code.Length &&
               code[position + 6] == (byte)Instruction.PUSH1 &&
               code[position + 7] == 0x00 &&
               code[position + 8] == (byte)Instruction.KECCAK256;
    }

    /// <summary>Offset past the end of a fused run beginning at <paramref name="position"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint EndOf(ReadOnlySpan<byte> code, nint position) =>
        position + (code[(int)position + 6] == (byte)Instruction.PUSH0 ? Push0WindowLength : LegacyWindowLength);

    /// <summary>Whether the run beginning at <paramref name="position"/> pushes its trailing zero with PUSH0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UsesPush0(ReadOnlySpan<byte> code, nint position) =>
        code[(int)position + 6] == (byte)Instruction.PUSH0;

    private static int OpcodeLength(byte opcode) =>
        opcode is >= (byte)Instruction.PUSH1 and <= (byte)Instruction.PUSH32
            ? opcode - (byte)Instruction.PUSH1 + 2
            : 1;

    private static long[] NewBitmap(int codeLength) => new long[(codeLength >> BitShiftPerInt64) + 1];

    private static void Set(long[] bitmap, int position) =>
        bitmap[position >> BitShiftPerInt64] |= 1L << (position & 63);
}
