// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class EvmStackTests
{
    // Regression coverage:
    // - Pop operations on empty stack must return the failure signal without mutating Head.
    //   Bug: previous Head-- post-decrement left Head = -1 on underflow (PopAddress, PopWord256).
    // - Push operations on full stack must return StackOverflow without mutating Head.
    //   Bug: tracer was called before the overflow check, recording phantom pushes.
    // - Pop round-trip for the single-, 2-, 3-, and 4-out UInt256 overloads.

    private const string PushByte = nameof(EvmStack.PushByte);
    private const string PushOne = nameof(EvmStack.PushOne);
    private const string PushZero = nameof(EvmStack.PushZero);
    private const string PushUInt32 = nameof(EvmStack.PushUInt32);
    private const string PushUInt64 = nameof(EvmStack.PushUInt64);
    private const string PushUInt256 = nameof(EvmStack.PushUInt256);
    private const string PushBytes = nameof(EvmStack.PushBytes);
    private const string Dup = nameof(EvmStack.Dup);
    private const string Swap = nameof(EvmStack.Swap);
    private const string Exchange = nameof(EvmStack.Exchange);

    private const string PopUInt256_1 = "PopUInt256";
    private const string PopUInt256_2 = "PopUInt256_2out";
    private const string PopUInt256_3 = "PopUInt256_3out";
    private const string PopUInt256_4 = "PopUInt256_4out";
    private const string PopWord256_out = "PopWord256_out";
    private const string PopAddress_out = "PopAddress_out";
    private const string PopLimbo = nameof(EvmStack.PopLimbo);

    [TestCase(PushByte)]
    [TestCase(PushOne)]
    [TestCase(PushZero)]
    [TestCase(PushUInt32)]
    [TestCase(PushUInt64)]
    [TestCase(PushUInt256)]
    [TestCase(PushBytes)]
    [TestCase(Dup)]
    public void Push_when_full_returns_StackOverflow_and_preserves_head(string op)
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);

        // Dup needs at least one element to duplicate; for pure pushes Head=Max-1 is enough.
        if (op == Dup) stack.PushOne<OffFlag>();
        stack.Head = EvmStack.MaxStackSize - 1;

        EvmExceptionType result = InvokePush(op, ref stack);

        result.Should().Be(EvmExceptionType.StackOverflow);
        stack.Head.Should().Be(EvmStack.MaxStackSize - 1);
    }

    [TestCase(PopUInt256_1, 0)]
    [TestCase(PopUInt256_2, 1)]
    [TestCase(PopUInt256_3, 2)]
    [TestCase(PopUInt256_4, 3)]
    [TestCase(PopWord256_out, 0)]
    [TestCase(PopAddress_out, 0)]
    [TestCase(PopLimbo, 0)]
    public void Pop_with_insufficient_depth_returns_false_and_preserves_head(string op, int preFilled)
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);
        for (int i = 0; i < preFilled; i++) stack.PushOne<OffFlag>();

        bool result = InvokePopBool(op, ref stack);

        result.Should().BeFalse();
        stack.Head.Should().Be(preFilled);
    }

    [TestCase(Dup)]
    [TestCase(Swap)]
    [TestCase(Exchange)]
    public void StackReshuffle_with_insufficient_depth_returns_StackUnderflow_and_preserves_head(string op)
    {
        // DUPN / SWAPN / EXCHANGE delegate through stack.Dup/Swap/Exchange; all three must
        // return StackUnderflow (not corrupt Head) when the addressed slot is past the bottom.
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);
        // One element on the stack; ops below ask for a slot we do not have.
        stack.PushOne<OffFlag>();

        EvmExceptionType result = op switch
        {
            Dup => stack.Dup<OffFlag>(2),            // need >= 2 elements
            Swap => stack.Swap<OffFlag>(2),          // swap top with 2nd, need >= 2
            Exchange => stack.Exchange<OffFlag>(1, 2), // need depth >= 2
            _ => throw new System.ArgumentOutOfRangeException(nameof(op), op, null),
        };

        result.Should().Be(EvmExceptionType.StackUnderflow);
        stack.Head.Should().Be(1);
    }

    [Test]
    public void PopByte_on_empty_returns_minus_one_and_preserves_head()
    {
        // Sentinel must be distinguishable from a legitimate zero byte; casting to byte
        // would silently produce 255 if the caller ignored the underflow signal.
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);

        int result = stack.PopByte();

        result.Should().Be(-1);
        stack.Head.Should().Be(0);
    }

    [Test]
    public void PopAddress_on_empty_returns_null_and_preserves_head()
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);

        Address? result = stack.PopAddress();

        result.Should().BeNull();
        stack.Head.Should().Be(0);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(5)]
    [TestCase(16)]
    [TestCase(31)]
    public void Truncated_PUSH32_preserves_leading_bytes_and_zero_pads_tail(int used)
    {
        // EVM spec: truncated PUSH{n} (where code ends before n bytes of immediate) must push
        // <available-bytes, 00...00> in big-endian. Available bytes go to the high end;
        // the missing tail is zero-filled. Regression guard for PushBothPaddedBytes in
        // the Op32.Push fallback for a PUSH32 at end of bytecode.
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);
        byte[] immediate = new byte[used];
        for (int i = 0; i < used; i++) immediate[i] = (byte)(0xA0 + i);

        EvmExceptionType result = stack.PushBothPaddedBytes<OffFlag>(
            ref MemoryMarshal.GetArrayDataReference(immediate),
            used,
            pushSize: 32);

        result.Should().Be(EvmExceptionType.None);
        stack.PopWord256(out Span<byte> word).Should().BeTrue();
        for (int i = 0; i < used; i++) word[i].Should().Be((byte)(0xA0 + i), $"byte {i} high-end");
        for (int i = used; i < 32; i++) word[i].Should().Be(0, $"byte {i} zero-pad tail");
    }

    [Test]
    public void PushUInt256_then_PopUInt256_roundtrip()
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);
        UInt256 value = new(0x1111111111111111UL, 0x2222222222222222UL, 0x3333333333333333UL, 0x4444444444444444UL);

        stack.PushUInt256<OffFlag>(in value).Should().Be(EvmExceptionType.None);
        stack.PopUInt256(out UInt256 popped).Should().BeTrue();

        popped.Should().Be(value);
        stack.Head.Should().Be(0);
    }

    [Test]
    public void Three_pushes_then_three_out_pop_returns_top_first()
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);
        UInt256 x = new(1), y = new(2), z = new(3);

        // Push in order x, y, z so z is top of stack.
        stack.PushUInt256<OffFlag>(in x).Should().Be(EvmExceptionType.None);
        stack.PushUInt256<OffFlag>(in y).Should().Be(EvmExceptionType.None);
        stack.PushUInt256<OffFlag>(in z).Should().Be(EvmExceptionType.None);

        // Multi-out pop returns top first: a=z (was top), b=y, c=x (deepest).
        stack.PopUInt256(out UInt256 a, out UInt256 b, out UInt256 c).Should().BeTrue();

        a.Should().Be(z);
        b.Should().Be(y);
        c.Should().Be(x);
        stack.Head.Should().Be(0);
    }

    // Direct-encoder coverage for the new PushN fast paths (PUSH1..PUSH32 normal path).
    // A single wrong lane or shift in Push{N}Bytes changes the constant observed by contracts
    // and breaks consensus. Verifies: low-end contains the N immediate bytes in the supplied
    // order; high-end (32-N bytes) is zero. Values 0xA0..0xA0+N-1 chosen so that byte-swap or
    // lane-swap regressions are immediately visible in the failure message.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(10)]
    [TestCase(11)]
    [TestCase(12)]
    [TestCase(13)]
    [TestCase(14)]
    [TestCase(15)]
    [TestCase(16)]
    [TestCase(17)]
    [TestCase(18)]
    [TestCase(19)]
    [TestCase(20)]
    [TestCase(21)]
    [TestCase(22)]
    [TestCase(23)]
    [TestCase(24)]
    [TestCase(25)]
    [TestCase(26)]
    [TestCase(27)]
    [TestCase(28)]
    [TestCase(29)]
    [TestCase(30)]
    [TestCase(31)]
    [TestCase(32)]
    public void PushNBytes_encodes_big_endian_low_padded_high_zero(int n)
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);
        byte[] immediate = new byte[n];
        for (int i = 0; i < n; i++) immediate[i] = (byte)(0xA0 + i);

        EvmExceptionType result = InvokePushNBytes(n, ref stack, ref MemoryMarshal.GetArrayDataReference(immediate));

        result.Should().Be(EvmExceptionType.None);
        stack.PopWord256(out Span<byte> word).Should().BeTrue();

        for (int i = 0; i < 32 - n; i++)
            word[i].Should().Be(0, $"high-end byte {i} must be zero for PUSH{n}");
        for (int i = 0; i < n; i++)
            word[32 - n + i].Should().Be((byte)(0xA0 + i), $"immediate byte {i} of PUSH{n}");
    }

    private static EvmExceptionType InvokePushNBytes(int n, ref EvmStack stack, ref byte imm) => n switch
    {
        1 => stack.PushByte<OffFlag>(imm),
        2 => stack.Push2Bytes<OffFlag>(ref imm),
        3 => stack.Push3Bytes<OffFlag>(ref imm),
        4 => stack.Push4Bytes<OffFlag>(ref imm),
        5 => stack.Push5Bytes<OffFlag>(ref imm),
        6 => stack.Push6Bytes<OffFlag>(ref imm),
        7 => stack.Push7Bytes<OffFlag>(ref imm),
        8 => stack.Push8Bytes<OffFlag>(ref imm),
        9 => stack.Push9Bytes<OffFlag>(ref imm),
        10 => stack.Push10Bytes<OffFlag>(ref imm),
        11 => stack.Push11Bytes<OffFlag>(ref imm),
        12 => stack.Push12Bytes<OffFlag>(ref imm),
        13 => stack.Push13Bytes<OffFlag>(ref imm),
        14 => stack.Push14Bytes<OffFlag>(ref imm),
        15 => stack.Push15Bytes<OffFlag>(ref imm),
        16 => stack.Push16Bytes<OffFlag>(ref imm),
        17 => stack.Push17Bytes<OffFlag>(ref imm),
        18 => stack.Push18Bytes<OffFlag>(ref imm),
        19 => stack.Push19Bytes<OffFlag>(ref imm),
        20 => stack.Push20Bytes<OffFlag>(ref imm),
        21 => stack.Push21Bytes<OffFlag>(ref imm),
        22 => stack.Push22Bytes<OffFlag>(ref imm),
        23 => stack.Push23Bytes<OffFlag>(ref imm),
        24 => stack.Push24Bytes<OffFlag>(ref imm),
        25 => stack.Push25Bytes<OffFlag>(ref imm),
        26 => stack.Push26Bytes<OffFlag>(ref imm),
        27 => stack.Push27Bytes<OffFlag>(ref imm),
        28 => stack.Push28Bytes<OffFlag>(ref imm),
        29 => stack.Push29Bytes<OffFlag>(ref imm),
        30 => stack.Push30Bytes<OffFlag>(ref imm),
        31 => stack.Push31Bytes<OffFlag>(ref imm),
        32 => stack.Push32Bytes<OffFlag>(ref imm),
        _ => throw new System.ArgumentOutOfRangeException(nameof(n), n, null),
    };

    private static EvmExceptionType InvokePush(string op, ref EvmStack stack) => op switch
    {
        PushByte => stack.PushByte<OffFlag>(42),
        PushOne => stack.PushOne<OffFlag>(),
        PushZero => stack.PushZero<OffFlag>(),
        PushUInt32 => stack.PushUInt32<OffFlag>(0xdeadbeef),
        PushUInt64 => stack.PushUInt64<OffFlag>(0xdeadbeefcafebabeUL),
        PushUInt256 => PushUInt256Value(ref stack),
        PushBytes => stack.PushBytes<OffFlag>(new byte[32]),
        Dup => stack.Dup<OffFlag>(1),
        _ => throw new System.ArgumentOutOfRangeException(nameof(op), op, null),
    };

    // Separate helper because `in` parameters cannot appear inline in a switch expression arm.
    private static EvmExceptionType PushUInt256Value(ref EvmStack stack)
    {
        UInt256 value = new(1, 2, 3, 4);
        return stack.PushUInt256<OffFlag>(in value);
    }

    private static bool InvokePopBool(string op, ref EvmStack stack) => op switch
    {
        PopUInt256_1 => stack.PopUInt256(out _),
        PopUInt256_2 => stack.PopUInt256(out _, out _),
        PopUInt256_3 => stack.PopUInt256(out _, out _, out _),
        PopUInt256_4 => stack.PopUInt256(out _, out _, out _, out _),
        PopWord256_out => stack.PopWord256(out _),
        PopAddress_out => stack.PopAddress(out _),
        PopLimbo => stack.PopLimbo(),
        _ => throw new System.ArgumentOutOfRangeException(nameof(op), op, null),
    };

    private static VmState<EthereumGasPolicy> CreateEvmState() =>
        VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromLong(10_000),
            ExecutionType.CALL,
            ExecutionEnvironment.Rent(null, null, null, null, 0, default, default, default),
            new StackAccessTracker(),
            Snapshot.Empty);
}
