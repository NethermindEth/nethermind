// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Test;

/// <summary>A minimal assembler: opcodes, pushes, and jump labels resolved when the code is built.</summary>
internal sealed class EvmAsm
{
    private readonly List<byte> _code = [];
    private readonly Dictionary<string, int> _marks = [];
    private readonly List<(int At, string Mark)> _fixups = [];

    public int Length => _code.Count;

    public EvmAsm Op(Instruction instruction)
    {
        _code.Add((byte)instruction);
        return this;
    }

    public EvmAsm Push(UInt256 value)
    {
        if (value.IsZero) return Op(Instruction.PUSH0);
        byte[] bytes = value.ToBigEndian();
        int skip = 0;
        while (bytes[skip] == 0) skip++;
        _code.Add((byte)(Instruction.PUSH1 + (byte)(31 - skip)));
        for (int i = skip; i < 32; i++) _code.Add(bytes[i]);
        return this;
    }

    public EvmAsm Push(Address address)
    {
        _code.Add((byte)Instruction.PUSH20);
        _code.AddRange(address.Bytes);
        return this;
    }

    /// <summary>A jump destination.</summary>
    public EvmAsm Label(string name)
    {
        _marks.Add(name, _code.Count);
        return Op(Instruction.JUMPDEST);
    }

    /// <summary>A position without a jump destination, such as where appended data starts.</summary>
    public EvmAsm Mark(string name)
    {
        _marks.Add(name, _code.Count);
        return this;
    }

    /// <summary>Pushes the position of <paramref name="name"/> as two bytes.</summary>
    public EvmAsm PushMark(string name)
    {
        _code.Add((byte)Instruction.PUSH2);
        _fixups.Add((_code.Count, name));
        _code.Add(0);
        _code.Add(0);
        return this;
    }

    public EvmAsm Data(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) _code.Add(b);
        return this;
    }

    public byte[] Build()
    {
        byte[] code = [.. _code];
        foreach ((int at, string mark) in _fixups)
        {
            int position = _marks[mark];
            code[at] = (byte)(position >> 8);
            code[at + 1] = (byte)position;
        }

        return code;
    }
}

/// <summary>
/// The contracts of the prewarming fixtures, one per shape of the cold tail: a pointer chain, a range read from call data,
/// a counter-driven batch guarded by zero checks, CREATE2 proxies that destroy themselves, storage deletions, a branch a
/// placeholder takes the other way, and the gated swap of the 7 Oct 2026 incident; and a payer that funds a poor sender.
/// </summary>
internal static class ColdTailContracts
{
    /// <summary>Walks a pointer chain from slot 0: reads slot v until it reads 0. Slot i holds i + 1 up to <see cref="ChainLength"/>.</summary>
    public static readonly Address Chain = new("0x00000000000000000000000000000000004e4d30");
    public const int ChainLength = 12;

    /// <summary>For i below CALLDATALOAD(32): SSTORE(CALLDATALOAD(0) + i, SLOAD(CALLDATALOAD(0) + i) + 1).</summary>
    public static readonly Address Walker = new("0x00000000000000000000000000000000004e4d31");

    /// <summary>
    /// A claim of CALLDATALOAD(0) fresh slots from the counter in slot 0 on: reverts unless each of them is still zero, sets
    /// each to one, then moves the counter past them. The next claim's slots follow from the counter the previous one
    /// left, as claimRank batches do.
    /// </summary>
    public static readonly Address Batch = new("0x00000000000000000000000000000000004e4d32");
    public static readonly UInt256 BatchCounter = 7;
    public static readonly UInt256 BatchBase = 0x10000;
    public static StorageCell BatchSlot(UInt256 counter, int i) => new(Batch, BatchBase + counter + (ulong)i);

    /// <summary>
    /// CREATE2s CALLDATALOAD(0) proxies with salts from CALLDATALOAD(32) on. Each proxy, while being created, calls
    /// <see cref="Registry"/> with its own address and destroys itself.
    /// </summary>
    public static readonly Address Claimer = new("0x00000000000000000000000000000000004e4d33");

    /// <summary>SSTORE(k, SLOAD(k) + 1) for k = CALLDATALOAD(0): a mapping keyed by the caller's address.</summary>
    public static readonly Address Registry = new("0x00000000000000000000000000000000004e4d34");

    /// <summary>Without call data zeroes slots 1 to <see cref="DeleterSlots"/>; with it stores their sum in slot 0x100.</summary>
    public static readonly Address Deleter = new("0x00000000000000000000000000000000004e4d35");
    public const int DeleterSlots = 8;

    /// <summary>Reads slots 0x200.. when slot 0 holds 1, and slots 0x300.. otherwise; slot 0 holds 2.</summary>
    public static readonly Address Brancher = new("0x00000000000000000000000000000000004e4d36");

    /// <summary>Without balance at the parent; a transaction that funds it closes the gate the two swaps check.</summary>
    public static readonly Address Gate = new("0x00000000000000000000000000000000004e4d37");
    public static readonly Address SwapThenCheck = new("0x00000000000000000000000000000000004e4d38");
    public static readonly Address CheckThenSwap = new("0x00000000000000000000000000000000004e4d39");

    /// <summary>Probe: without call data SSTORE(0, 0); with call data SSTORE(1, SLOAD(0)). Slot 0 holds 0x4e4d at the parent.</summary>
    public static readonly Address Zeroer = new("0x00000000000000000000000000000000004e4d3a");

    /// <summary>Sends <see cref="Payout"/> to its caller.</summary>
    public static readonly Address BigPayer = new("0x00000000000000000000000000000000004e4d3b");
    public static readonly UInt256 Payout = 2.Ether;

    /// <summary>A sender with <see cref="PoorBalance"/> at the parent: less than the payout it gets from <see cref="BigPayer"/>.</summary>
    public static readonly PrivateKey Poor = TestItem.PrivateKeys[0x4e];
    public static readonly UInt256 PoorBalance = 1.Ether;

    public static readonly byte[] ProxyInitCode = new EvmAsm()
        .Op(Instruction.ADDRESS).Op(Instruction.PUSH0).Op(Instruction.MSTORE)
        .Op(Instruction.PUSH0).Op(Instruction.PUSH0).Push(32).Op(Instruction.PUSH0).Op(Instruction.PUSH0).Push(Registry)
        .Op(Instruction.GAS).Op(Instruction.CALL).Op(Instruction.POP)
        .Op(Instruction.CALLER).Op(Instruction.SELFDESTRUCT)
        .Build();

    /// <summary>The proxy <see cref="Claimer"/> creates with <paramref name="salt"/>.</summary>
    public static Address Proxy(UInt256 salt) => ContractAddress.From(Claimer, salt.ToBigEndian(), ProxyInitCode);

    /// <summary>Deploys the contracts and their parent-state storage.</summary>
    public static void Deploy(IWorldState state, IReleaseSpec spec)
    {
        Install(state, spec, Chain, ChainCode());
        for (int i = 0; i < ChainLength; i++) state.Set(new StorageCell(Chain, (ulong)i), (ulong)(i + 1));
        Install(state, spec, Walker, WalkerCode());
        state.Set(new StorageCell(Walker, 0x4e4d), 1);
        Install(state, spec, Batch, BatchCode());
        state.Set(new StorageCell(Batch, 0), BatchCounter);
        Install(state, spec, Claimer, ClaimerCode());
        Install(state, spec, Registry, RegistryCode());
        state.Set(new StorageCell(Registry, 0x4e4d), 1);
        Install(state, spec, Deleter, DeleterCode());
        for (int i = 1; i <= DeleterSlots; i++) state.Set(new StorageCell(Deleter, (ulong)i), (ulong)(0x4e4d + i));
        Install(state, spec, Brancher, BrancherCode());
        state.Set(new StorageCell(Brancher, 0), 2);
        Install(state, spec, SwapThenCheck, GatedSwapCode(writeFirst: true));
        Install(state, spec, CheckThenSwap, GatedSwapCode(writeFirst: false));
        state.Set(new StorageCell(SwapThenCheck, 1), 100);
        state.Set(new StorageCell(CheckThenSwap, 1), 100);
        Install(state, spec, Zeroer, [0x36, 0x60, 0x08, 0x57, 0x5F, 0x5F, 0x55, 0x00, 0x5B, 0x5F, 0x54, 0x60, 0x01, 0x55, 0x00]);
        state.Set(new StorageCell(Zeroer, 0), 0x4e4d);
        Install(state, spec, BigPayer, new EvmAsm()
            .Op(Instruction.PUSH0).Op(Instruction.PUSH0).Op(Instruction.PUSH0).Op(Instruction.PUSH0).Push(Payout)
            .Op(Instruction.CALLER).Op(Instruction.GAS).Op(Instruction.CALL).Op(Instruction.POP).Op(Instruction.STOP)
            .Build());
        state.AddToBalance(BigPayer, 100.Ether, spec);
        state.CreateAccount(Poor.Address, PoorBalance);
    }

    private static void Install(IWorldState state, IReleaseSpec spec, Address address, byte[] code)
    {
        state.CreateAccount(address, 0);
        state.InsertCode(address, Keccak.Compute(code), code, spec);
    }

    /// <summary>Two words of call data.</summary>
    public static byte[] Words(UInt256 first, UInt256 second) => [.. first.ToBigEndian(), .. second.ToBigEndian()];

    /// <summary>One word of call data.</summary>
    public static byte[] Word(UInt256 value) => value.ToBigEndian();

    private static byte[] ChainCode() => new EvmAsm()
        .Op(Instruction.PUSH0)
        .Label("loop")
        .Op(Instruction.SLOAD)
        .Op(Instruction.DUP1).Op(Instruction.ISZERO).PushMark("end").Op(Instruction.JUMPI)
        .PushMark("loop").Op(Instruction.JUMP)
        .Label("end")
        .Op(Instruction.STOP)
        .Build();

    private static byte[] WalkerCode() => new EvmAsm()
        // mem[0x40] = i = 0
        .Op(Instruction.PUSH0).Push(0x40).Op(Instruction.MSTORE)
        .Label("loop")
        // if !(i < CALLDATALOAD(32)) goto end
        .Push(32).Op(Instruction.CALLDATALOAD).Push(0x40).Op(Instruction.MLOAD).Op(Instruction.LT).Op(Instruction.ISZERO)
        .PushMark("end").Op(Instruction.JUMPI)
        // k = CALLDATALOAD(0) + i; SSTORE(k, SLOAD(k) + 1)
        .Push(0x40).Op(Instruction.MLOAD).Op(Instruction.PUSH0).Op(Instruction.CALLDATALOAD).Op(Instruction.ADD)
        .Op(Instruction.DUP1).Op(Instruction.SLOAD).Push(1).Op(Instruction.ADD).Op(Instruction.SWAP1).Op(Instruction.SSTORE)
        // i++
        .Push(0x40).Op(Instruction.MLOAD).Push(1).Op(Instruction.ADD).Push(0x40).Op(Instruction.MSTORE)
        .PushMark("loop").Op(Instruction.JUMP)
        .Label("end")
        .Op(Instruction.STOP)
        .Build();

    private static byte[] BatchCode() => new EvmAsm()
        // mem[0] = c = SLOAD(0); mem[0x20] = n = CALLDATALOAD(0); mem[0x40] = i = 0
        .Op(Instruction.PUSH0).Op(Instruction.SLOAD).Op(Instruction.PUSH0).Op(Instruction.MSTORE)
        .Op(Instruction.PUSH0).Op(Instruction.CALLDATALOAD).Push(0x20).Op(Instruction.MSTORE)
        .Op(Instruction.PUSH0).Push(0x40).Op(Instruction.MSTORE)
        .Label("loop")
        .Push(0x20).Op(Instruction.MLOAD).Push(0x40).Op(Instruction.MLOAD).Op(Instruction.LT).Op(Instruction.ISZERO)
        .PushMark("done").Op(Instruction.JUMPI)
        // k = BatchBase + i + c; revert unless SLOAD(k) == 0; SSTORE(k, 1)
        .Push(BatchBase).Push(0x40).Op(Instruction.MLOAD).Op(Instruction.ADD).Op(Instruction.PUSH0).Op(Instruction.MLOAD).Op(Instruction.ADD)
        .Op(Instruction.DUP1).Op(Instruction.SLOAD).PushMark("fail").Op(Instruction.JUMPI)
        .Push(1).Op(Instruction.SWAP1).Op(Instruction.SSTORE)
        .Push(0x40).Op(Instruction.MLOAD).Push(1).Op(Instruction.ADD).Push(0x40).Op(Instruction.MSTORE)
        .PushMark("loop").Op(Instruction.JUMP)
        .Label("done")
        // SSTORE(0, c + n)
        .Push(0x20).Op(Instruction.MLOAD).Op(Instruction.PUSH0).Op(Instruction.MLOAD).Op(Instruction.ADD).Op(Instruction.PUSH0).Op(Instruction.SSTORE)
        .Op(Instruction.STOP)
        .Label("fail")
        .Op(Instruction.PUSH0).Op(Instruction.PUSH0).Op(Instruction.REVERT)
        .Build();

    private static byte[] ClaimerCode() => new EvmAsm()
        // The proxy's init code to memory at 0x100.
        .Push((ulong)ProxyInitCode.Length).PushMark("init").Push(0x100).Op(Instruction.CODECOPY)
        // mem[0] = n, mem[0x20] = first salt, mem[0x40] = i = 0
        .Op(Instruction.PUSH0).Op(Instruction.CALLDATALOAD).Op(Instruction.PUSH0).Op(Instruction.MSTORE)
        .Push(0x20).Op(Instruction.CALLDATALOAD).Push(0x20).Op(Instruction.MSTORE)
        .Op(Instruction.PUSH0).Push(0x40).Op(Instruction.MSTORE)
        .Label("loop")
        .Op(Instruction.PUSH0).Op(Instruction.MLOAD).Push(0x40).Op(Instruction.MLOAD).Op(Instruction.LT).Op(Instruction.ISZERO)
        .PushMark("done").Op(Instruction.JUMPI)
        // CREATE2(0, 0x100, init length, first salt + i)
        .Push(0x40).Op(Instruction.MLOAD).Push(0x20).Op(Instruction.MLOAD).Op(Instruction.ADD)
        .Push((ulong)ProxyInitCode.Length).Push(0x100).Op(Instruction.PUSH0).Op(Instruction.CREATE2).Op(Instruction.POP)
        .Push(0x40).Op(Instruction.MLOAD).Push(1).Op(Instruction.ADD).Push(0x40).Op(Instruction.MSTORE)
        .PushMark("loop").Op(Instruction.JUMP)
        .Label("done")
        .Op(Instruction.STOP)
        .Mark("init")
        .Data(ProxyInitCode)
        .Build();

    private static byte[] RegistryCode() => new EvmAsm()
        .Op(Instruction.PUSH0).Op(Instruction.CALLDATALOAD).Op(Instruction.DUP1).Op(Instruction.SLOAD).Push(1).Op(Instruction.ADD)
        .Op(Instruction.SWAP1).Op(Instruction.SSTORE)
        .Op(Instruction.STOP)
        .Build();

    private static byte[] DeleterCode()
    {
        EvmAsm asm = new EvmAsm().Op(Instruction.CALLDATASIZE).PushMark("sum").Op(Instruction.JUMPI);
        for (int i = 1; i <= DeleterSlots; i++) asm.Op(Instruction.PUSH0).Push((ulong)i).Op(Instruction.SSTORE);
        asm.Op(Instruction.STOP).Label("sum").Op(Instruction.PUSH0);
        for (int i = 1; i <= DeleterSlots; i++) asm.Push((ulong)i).Op(Instruction.SLOAD).Op(Instruction.ADD);
        return asm.Push(0x100).Op(Instruction.SSTORE).Op(Instruction.STOP).Build();
    }

    private static byte[] BrancherCode()
    {
        EvmAsm asm = new EvmAsm()
            .Op(Instruction.PUSH0).Op(Instruction.SLOAD).Push(1).Op(Instruction.EQ).PushMark("one").Op(Instruction.JUMPI);
        for (int i = 0; i < 8; i++) asm.Push((ulong)(0x300 + i)).Op(Instruction.SLOAD).Op(Instruction.POP);
        asm.Op(Instruction.STOP).Label("one");
        for (int i = 0; i < 8; i++) asm.Push((ulong)(0x200 + i)).Op(Instruction.SLOAD).Op(Instruction.POP);
        return asm.Op(Instruction.STOP).Build();
    }

    /// <summary>
    /// Without call data, a swap: SSTORE(1, SLOAD(1) - 25), and REVERT if <see cref="Gate"/> has a balance, in the order
    /// <paramref name="writeFirst"/> gives. With call data, a later spend: SSTORE(1, SLOAD(1) - 4).
    /// </summary>
    private static byte[] GatedSwapCode(bool writeFirst)
    {
        byte[] debit = [0x60, 0x19, 0x60, 0x01, 0x54, 0x03, 0x60, 0x01, 0x55];
        byte[] gate = [0x73, .. Gate.Bytes, 0x31, 0x60, 0x27, 0x57];
        return [0x36, 0x60, 0x2B, 0x57, .. writeFirst ? debit : gate, .. writeFirst ? gate : debit,
            0x00, 0x5B, 0x5F, 0x5F, 0xFD, 0x5B, 0x60, 0x04, 0x60, 0x01, 0x54, 0x03, 0x60, 0x01, 0x55, 0x00];
    }
}
