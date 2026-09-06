// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class EvmStackTests
{
    [Test]
    public void UInt256_writeback_preserves_aliases_and_unaligned_slots(
        [Values(0, 1, 7, 8, 31)] int offset, [Values] bool alias)
    {
        byte[] buffer = new byte[offset + EvmPooledMemory.WordSize + 1];
        Array.Fill(buffer, (byte)0xa5);
        UInt256 value = new(0x0123456789abcdef, 0xfedcba9876543210, 0x1122334455667788, 0x8877665544332211);
        byte[] expected = value.ToBigEndian();
        ref byte slot = ref buffer[offset];
        Unsafe.WriteUnaligned(ref slot, value);
        ref UInt256 source = ref (alias ? ref Unsafe.As<byte, UInt256>(ref slot) : ref value);

        EvmStack.WriteUInt256ToSlot(ref slot, in source);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.AsSpan(offset, EvmPooledMemory.WordSize).ToArray(), Is.EqualTo(expected));
            Assert.That(buffer[^1], Is.EqualTo(0xa5));
            if (offset > 0) Assert.That(buffer[offset - 1], Is.EqualTo(0xa5));
        }
    }

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

        Assert.That(result, Is.EqualTo(EvmExceptionType.StackOverflow));
        Assert.That((int)stack.Head, Is.EqualTo(EvmStack.MaxStackSize - 1));
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

        Assert.That(result, Is.False);
        Assert.That((int)stack.Head, Is.EqualTo(preFilled));
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
            Dup => stack.Dup<OffFlag, OnFlag>(2),            // need >= 2 elements
            Swap => stack.Swap<OffFlag, OnFlag>(2),          // swap top with 2nd, need >= 2
            Exchange => stack.Exchange<OffFlag>(1, 2), // need depth >= 2
            _ => throw new System.ArgumentOutOfRangeException(nameof(op), op, null),
        };

        Assert.That(result, Is.EqualTo(EvmExceptionType.StackUnderflow));
        Assert.That((int)stack.Head, Is.EqualTo(1));
    }

    [Test]
    public void PopByte_on_empty_returns_minus_one_and_preserves_head()
    {
        // Sentinel must be distinguishable from a legitimate zero byte; casting to byte
        // would silently produce 255 if the caller ignored the underflow signal.
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);

        int result = stack.PopByte();

        Assert.That(result, Is.EqualTo(-1));
        Assert.That((int)stack.Head, Is.EqualTo(0));
    }

    [Test]
    public void PopAddress_on_empty_returns_null_and_preserves_head()
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);

        Address? result = stack.PopAddress();

        Assert.That(result, Is.Null);
        Assert.That((int)stack.Head, Is.EqualTo(0));
    }

    [Test]
    public void Truncated_PUSH32_preserves_leading_bytes_and_zero_pads_tail([Values(0, 1, 5, 16, 31)] int used, [Values] bool checkDepth)
    {
        // EVM spec: truncated PUSH{n} (where code ends before n bytes of immediate) must push
        // <available-bytes, 00...00> in big-endian. Available bytes go to the high end;
        // the missing tail is zero-filled. Regression guard for PushBothPaddedBytes in
        // the Op32.Push fallback for a PUSH32 at end of bytecode.
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);
        byte[] immediate = new byte[used];
        for (int i = 0; i < used; i++) immediate[i] = (byte)(0xA0 + i);

        EvmExceptionType result = checkDepth
            ? stack.PushBothPaddedBytes<OffFlag, OnFlag>(ref MemoryMarshal.GetArrayDataReference(immediate), used, 32)
            : stack.PushBothPaddedBytes<OffFlag, OffFlag>(ref MemoryMarshal.GetArrayDataReference(immediate), used, 32);

        Assert.That(result, Is.EqualTo(EvmExceptionType.None));
        Assert.That(stack.PopWord256(out Span<byte> word), Is.True);
        for (int i = 0; i < used; i++) Assert.That(word[i], Is.EqualTo((byte)(0xA0 + i)), $"byte {i} high-end");
        for (int i = used; i < 32; i++) Assert.That(word[i], Is.EqualTo(0), $"byte {i} zero-pad tail");
    }

    [Test]
    public void Truncated_PUSH_reports_the_completed_word([Range(2, 32)] int width, [Values] bool hasData)
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        int used = hasData ? width - 1 : 0;
        byte[] immediate = new byte[used];
        byte[] expected = new byte[32];
        for (int i = 0; i < used; i++) expected[32 - width + i] = immediate[i] = (byte)(0xa0 + i);
        StackPushTracer tracer = new();
        vmState.InitializeStacks(tracer, default, out EvmStack stack);

        EvmExceptionType result = stack.PushBothPaddedBytes<OnFlag, OnFlag>(ref MemoryMarshal.GetArrayDataReference(immediate), used, width);

        Assert.That(stack.PopUInt256(out UInt256 value), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(EvmExceptionType.None));
            Assert.That(value, Is.EqualTo(new UInt256(expected, isBigEndian: true)));
            Assert.That(tracer.StackItem, Is.EqualTo(expected));
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void Traced_PUSH2_zero_pads_missing_immediate_bytes(int used)
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        byte[] immediate = new byte[used];
        for (int i = 0; i < used; i++) immediate[i] = (byte)(0xa0 + i);
        StackPushTracer tracer = new();
        vmState.InitializeStacks(tracer, immediate, out EvmStack stack);
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(GasCostOf.VeryLow);
        nint pc = 0;

        EvmExceptionType result = EvmInstructions.InstructionPush2<EthereumGasPolicy, OnFlag>(ref stack, ref gas, null!, ref pc);

        UInt256 expected = used switch { 0 => 0, 1 => 0xa000, _ => 0xa0a1 };
        Assert.That(stack.PopUInt256(out UInt256 value), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(EvmExceptionType.None));
            Assert.That(value, Is.EqualTo(expected));
            Assert.That(new UInt256(tracer.StackItem, isBigEndian: true), Is.EqualTo(expected));
            Assert.That(pc, Is.EqualTo((nint)2));
            Assert.That(EthereumGasPolicy.GetRemainingGas(in gas), Is.Zero);
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(17)]
    [TestCase(31)]
    [TestCase(32)]
    public void PushRightPaddedBytes_traces_the_completed_word(int length)
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        StackPushTracer tracer = new();
        vmState.InitializeStacks(tracer, default, out EvmStack stack);
        byte[] source = new byte[EvmPooledMemory.WordSize];
        for (int i = 0; i < source.Length; i++) source[i] = (byte)(i + 1);
        byte[] expected = new byte[EvmPooledMemory.WordSize];
        source.AsSpan(0, length).CopyTo(expected);

        EvmExceptionType result = stack.PushRightPaddedBytes<OnFlag>(
            ref MemoryMarshal.GetArrayDataReference(source),
            (uint)length);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(EvmExceptionType.None));
            Assert.That(tracer.StackItem, Is.EqualTo(expected));
            Assert.That(stack.PopWord256(out Span<byte> word), Is.True);
            Assert.That(word.ToArray(), Is.EqualTo(expected));
        }
    }

    [Test]
    public void PushUInt256_then_PopUInt256_roundtrip()
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);
        UInt256 value = new(0x1111111111111111UL, 0x2222222222222222UL, 0x3333333333333333UL, 0x4444444444444444UL);

        Assert.That(stack.PushUInt256<OffFlag>(in value), Is.EqualTo(EvmExceptionType.None));
        Assert.That(stack.PopUInt256(out UInt256 popped), Is.True);

        Assert.That(popped, Is.EqualTo(value));
        Assert.That((int)stack.Head, Is.EqualTo(0));
    }

    [Test]
    public void Three_pushes_then_three_out_pop_returns_top_first()
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);
        UInt256 x = new(1), y = new(2), z = new(3);

        // Push in order x, y, z so z is top of stack.
        Assert.That(stack.PushUInt256<OffFlag>(in x), Is.EqualTo(EvmExceptionType.None));
        Assert.That(stack.PushUInt256<OffFlag>(in y), Is.EqualTo(EvmExceptionType.None));
        Assert.That(stack.PushUInt256<OffFlag>(in z), Is.EqualTo(EvmExceptionType.None));

        // Multi-out pop returns top first: a=z (was top), b=y, c=x (deepest).
        Assert.That(stack.PopUInt256(out UInt256 a, out UInt256 b, out UInt256 c), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(a, Is.EqualTo(z));
            Assert.That(b, Is.EqualTo(y));
            Assert.That(c, Is.EqualTo(x));
            Assert.That((int)stack.Head, Is.EqualTo(0));
        }
    }

    // Direct-encoder coverage for the new PushN fast paths (PUSH1..PUSH32 normal path).
    // A single wrong lane or shift in Push{N}Bytes changes the constant observed by contracts
    // and breaks consensus. Verifies: low-end contains the N immediate bytes in the supplied
    // order; high-end (32-N bytes) is zero. Values 0xA0..0xA0+N-1 chosen so that byte-swap or
    // lane-swap regressions are immediately visible in the failure message.
    [Test]
    public void PushNBytes_encodes_big_endian_low_padded_high_zero([Range(1, 32)] int n, [Values] bool checkDepth)
    {
        using VmState<EthereumGasPolicy> vmState = CreateEvmState();
        vmState.InitializeStacks(default, out EvmStack stack);
        byte[] immediate = new byte[n];
        for (int i = 0; i < n; i++) immediate[i] = (byte)(0xA0 + i);

        EvmExceptionType result = checkDepth
            ? InvokePushNBytes<OnFlag>(n, ref stack, ref MemoryMarshal.GetArrayDataReference(immediate))
            : InvokePushNBytes<OffFlag>(n, ref stack, ref MemoryMarshal.GetArrayDataReference(immediate));

        Assert.That(result, Is.EqualTo(EvmExceptionType.None));
        Assert.That(stack.PopWord256(out Span<byte> word), Is.True);

        for (int i = 0; i < 32 - n; i++)
            Assert.That(word[i], Is.EqualTo(0), $"high-end byte {i} must be zero for PUSH{n}");
        for (int i = 0; i < n; i++)
            Assert.That(word[32 - n + i], Is.EqualTo((byte)(0xA0 + i)), $"immediate byte {i} of PUSH{n}");
    }

    private static EvmExceptionType InvokePushNBytes<TCheckDepth>(int n, ref EvmStack stack, ref byte imm) where TCheckDepth : struct, IFlag => n switch
    {
        1 => stack.PushByte<OffFlag, TCheckDepth>(imm),
        2 => stack.Push2Bytes<OffFlag, TCheckDepth>(ref imm),
        3 => stack.Push3Bytes<OffFlag, TCheckDepth>(ref imm),
        4 => stack.Push4Bytes<OffFlag, TCheckDepth>(ref imm),
        5 => stack.Push5Bytes<OffFlag, TCheckDepth>(ref imm),
        6 => stack.Push6Bytes<OffFlag, TCheckDepth>(ref imm),
        7 => stack.Push7Bytes<OffFlag, TCheckDepth>(ref imm),
        8 => stack.Push8Bytes<OffFlag, TCheckDepth>(ref imm),
        9 => stack.Push9Bytes<OffFlag, TCheckDepth>(ref imm),
        10 => stack.Push10Bytes<OffFlag, TCheckDepth>(ref imm),
        11 => stack.Push11Bytes<OffFlag, TCheckDepth>(ref imm),
        12 => stack.Push12Bytes<OffFlag, TCheckDepth>(ref imm),
        13 => stack.Push13Bytes<OffFlag, TCheckDepth>(ref imm),
        14 => stack.Push14Bytes<OffFlag, TCheckDepth>(ref imm),
        15 => stack.Push15Bytes<OffFlag, TCheckDepth>(ref imm),
        16 => stack.Push16Bytes<OffFlag, TCheckDepth>(ref imm),
        17 => stack.Push17Bytes<OffFlag, TCheckDepth>(ref imm),
        18 => stack.Push18Bytes<OffFlag, TCheckDepth>(ref imm),
        19 => stack.Push19Bytes<OffFlag, TCheckDepth>(ref imm),
        20 => stack.Push20Bytes<OffFlag, TCheckDepth>(ref imm),
        21 => stack.Push21Bytes<OffFlag, TCheckDepth>(ref imm),
        22 => stack.Push22Bytes<OffFlag, TCheckDepth>(ref imm),
        23 => stack.Push23Bytes<OffFlag, TCheckDepth>(ref imm),
        24 => stack.Push24Bytes<OffFlag, TCheckDepth>(ref imm),
        25 => stack.Push25Bytes<OffFlag, TCheckDepth>(ref imm),
        26 => stack.Push26Bytes<OffFlag, TCheckDepth>(ref imm),
        27 => stack.Push27Bytes<OffFlag, TCheckDepth>(ref imm),
        28 => stack.Push28Bytes<OffFlag, TCheckDepth>(ref imm),
        29 => stack.Push29Bytes<OffFlag, TCheckDepth>(ref imm),
        30 => stack.Push30Bytes<OffFlag, TCheckDepth>(ref imm),
        31 => stack.Push31Bytes<OffFlag, TCheckDepth>(ref imm),
        32 => stack.Push32Bytes<OffFlag, TCheckDepth>(ref imm),
        _ => throw new System.ArgumentOutOfRangeException(nameof(n), n, null),
    };

    private static EvmExceptionType InvokePush(string op, ref EvmStack stack) => op switch
    {
        PushByte => stack.PushByte<OffFlag, OnFlag>(42),
        PushOne => stack.PushOne<OffFlag>(),
        PushZero => stack.PushZero<OffFlag, OnFlag>(),
        PushUInt32 => stack.PushUInt32<OffFlag, OnFlag>(0xdeadbeef),
        PushUInt64 => stack.PushUInt64<OffFlag, OnFlag>(0xdeadbeefcafebabeUL),
        PushUInt256 => PushUInt256Value(ref stack),
        PushBytes => stack.PushBytes<OffFlag>(new byte[32]),
        Dup => stack.Dup<OffFlag, OnFlag>(1),
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
            EthereumGasPolicy.FromULong(10_000UL),
            ExecutionType.CALL,
            ExecutionEnvironment.Rent(null, null, null, null, 0, default, default),
            new StackAccessTracker(),
            Snapshot.Empty);

    private sealed class StackPushTracer : TxTracer
    {
        public byte[] StackItem { get; private set; } = [];

        public override void ReportStackPush(in ReadOnlySpan<byte> stackItem) => StackItem = stackItem.ToArray();
    }
}
