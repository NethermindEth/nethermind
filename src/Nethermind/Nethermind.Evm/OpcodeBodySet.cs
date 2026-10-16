// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Nethermind.Evm;

/// <summary>How a dispatch-table entry continues once its body has run.</summary>
[Experimental("NMEVM0001")]
public enum OpcodeFlow : byte
{
    /// <summary>The table build recorded nothing for the entry.</summary>
    Unrecorded,
    /// <summary>The frame goes on with the next opcode, or wherever the body left the program counter.</summary>
    Continues,
    /// <summary>The body ends the frame.</summary>
    Terminates,
    /// <summary>JUMPI, which its handler runs itself.</summary>
    ConditionalJump,
}

/// <summary>One untraced dispatch-table entry: the body it runs, and what that body declares about itself.</summary>
[Experimental("NMEVM0001")]
public sealed class OpcodeBody
{
    internal static readonly OpcodeBody Unrecorded = new(null, OpcodeFlow.Unrecorded, isDefined: false, hasCheckedBody: false, usesVm: false,
        chargesFixedGas: false, mayJump: false, hasUntracedFastPath: false, stackInputs: 0, stackGrowth: 0, fixedGas: 0);

    internal OpcodeBody(Type? bodyType, OpcodeFlow flow, bool isDefined, bool hasCheckedBody, bool usesVm, bool chargesFixedGas, bool mayJump,
        bool hasUntracedFastPath, int stackInputs, int stackGrowth, ulong fixedGas)
    {
        BodyType = bodyType;
        Flow = flow;
        IsDefined = isDefined;
        HasCheckedBody = hasCheckedBody;
        UsesVm = usesVm;
        ChargesFixedGas = chargesFixedGas;
        MayJump = mayJump;
        HasUntracedFastPath = hasUntracedFastPath;
        StackInputs = stackInputs;
        StackGrowth = stackGrowth;
        FixedGas = fixedGas;
    }

    /// <summary>The body's type; null for JUMPI, which its handler runs, and for unrecorded entries.</summary>
    /// <remarks>
    /// Its public static <c>Execute(ref EvmStack, ref TGasPolicy, VirtualMachine&lt;TGasPolicy&gt;, ref nint)</c>
    /// receives the program counter past the opcode byte. It may leave the counter past instructions it fuses with,
    /// so a caller continues from the counter it leaves.
    /// </remarks>
    public Type? BodyType { get; }

    public OpcodeFlow Flow { get; }

    /// <summary>False for the bad-instruction body undefined opcodes run.</summary>
    public bool IsDefined { get; }

    /// <summary>Dispatch charges <see cref="FixedGas"/> and checks <see cref="StackInputs"/> and <see cref="StackGrowth"/>; Execute cannot fail.</summary>
    public bool HasCheckedBody { get; }

    /// <summary>Whether a checked Execute reads the virtual machine; dispatch may pass null to one that does not.</summary>
    public bool UsesVm { get; }

    /// <summary>Whether every charge an unchecked Execute makes is a fixed execution-gas cost.</summary>
    public bool ChargesFixedGas { get; }

    /// <summary>Whether Execute can move the program counter to a jump target.</summary>
    public bool MayJump { get; }

    /// <summary>Whether untraced dispatch tries the body's fast path before its plain Execute.</summary>
    public bool HasUntracedFastPath { get; }

    /// <summary>The words a checked body or JUMPI reads from the stack.</summary>
    public int StackInputs { get; }

    /// <summary>The net change in stack height a checked body or JUMPI makes.</summary>
    public int StackGrowth { get; }

    /// <summary>
    /// What a checked body's gas check charges, measured on a probe, or JUMPI's charge; otherwise 0.
    /// <see cref="ulong.MaxValue"/> where the probe was refused.
    /// </summary>
    public ulong FixedGas { get; }
}

/// <summary>What every entry of an untraced dispatch table runs; equal sets are one shared instance per process.</summary>
[Experimental("NMEVM0001")]
public sealed class OpcodeBodySet
{
    private static int s_ids;

    internal OpcodeBodySet(OpcodeBody[] bodies, bool usesCallDestinations)
    {
        Bodies = bodies;
        UsesCallDestinations = usesCallDestinations;
        Id = Interlocked.Increment(ref s_ids);
    }

    /// <summary>256 entries, indexed by opcode.</summary>
    public IReadOnlyList<OpcodeBody> Bodies { get; }

    /// <summary>Whether EIP-7979 subroutines are enabled.</summary>
    public bool UsesCallDestinations { get; }

    /// <summary>Unique within the process; for logs and cache keys.</summary>
    public int Id { get; }
}
