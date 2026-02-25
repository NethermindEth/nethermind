// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Benchmarks that execute real EVM instruction handlers via function pointer dispatch,
/// matching the production execution path in VirtualMachine.
/// Stack values are prepared per benchmark case based on <see cref="Opcode"/>.
/// Run: dotnet run -c Release --filter "*EvmOpcodesBenchmark*"
/// </summary>
[Config(typeof(EvmOpcodesBenchmarkConfig))]
public unsafe class EvmOpcodesBenchmark
{
    private const int InnerCount = 4096;
    private const int KeccakWordSize = EvmStack.WordSize;

    private delegate*<VirtualMachine<EthereumGasPolicy>, ref EvmStack, ref EthereumGasPolicy, ref int, EvmExceptionType>[] _opcodes = null!;
    private BenchmarkVm _vm = null!;
    private byte[] _stackBuffer = null!;
    private EthereumGasPolicy _gas;
    private ExecutionEnvironment _env = null!;
    private VmState<EthereumGasPolicy> _vmState = null!;
    private IWorldState _stateProvider = null!;
    private IDisposable _stateScope = null!;
    private int _stackDepth;
    private int _runsPerBatch;
    private int _iterationId;
    private UInt256[] _dynamicStorageKeys = null!;
    private UInt256[] _dynamicKeccakOffsets = null!;

    // Full-width 256-bit test values for 2-param ops (a is popped first from slot[1], b from slot[0]).
    private static readonly UInt256 ValueA = UInt256.Parse("0x6F1D2C3B4A59687766554433221100FFEEDDCCBBAA99887766554433221100FF");
    private static readonly UInt256 ValueB = UInt256.Parse("0x5A0F9E8D7C6B5A4938271605F4E3D2C1B0A99887766554433221100FFEDCBA98");
    private static readonly UInt256 StarkFieldModulus = UInt256.Parse("0x0800000000000011000000000000000000000000000000000000000000000001");
    private static readonly UInt256 TernaryOperandA = UInt256.MaxValue - new UInt256(0x12345UL);
    private static readonly UInt256 TernaryOperandB = UInt256.MaxValue - new UInt256(0xABCDEUL);
    private static readonly UInt256 MulOperandA = UInt256.Parse("0xF91D2C3B4A59687766554433221100FFEEDDCCBBAA99887766554433221100F1");
    private static readonly UInt256 MulOperandB = UInt256.Parse("0xD50F9E8D7C6B5A4938271605F4E3D2C1B0A99887766554433221100FFEDCBAE3");
    private static readonly UInt256 DivisorOperand = UInt256.Parse("0x2B7D4E1943A6C1E28F30517294B6D8FA1C3E507294B6D8FA1C3E507294B6D8F9");
    private static readonly UInt256 DividendOperand = UInt256.Parse("0xE4A39F6C2D18B57A93C4E1F6287D3A5CB7E9D1F30496A8BCD2E4F6A8C1D3E5F7");
    private static readonly UInt256 ShiftAmount = new(64);
    private static readonly UInt256 BytePosition = new(15);
    private static readonly UInt256 SignExtendPosition = new(15);
    private static readonly UInt256 JumpDestination = UInt256.Zero;
    private static readonly UInt256 One = UInt256.One;
    private static readonly UInt256 CallTarget = new(0x1000UL);
    private static readonly UInt256 CallGasLimit = new(100_000UL);
    private static readonly UInt256 DynamicStorageBase = new(1_000_000UL);
    private static readonly UInt256 KeccakLength = new((ulong)KeccakWordSize);
    private static readonly Address CallTargetAddress = Address.FromNumber(0x1000);
    private static readonly byte[] StopCode = [(byte)Instruction.STOP];
    private static readonly Instruction[] AllValidLegacyOpcodes = Enum
        .GetValues<Instruction>()
        .Where(static opcode => opcode.IsValid(isEofContext: false) && opcode != Instruction.INVALID)
        .ToArray();
    private static readonly Instruction[] PerRunRefreshedOpcodes =
    [
        Instruction.KECCAK256,
        Instruction.SLOAD,
        Instruction.SSTORE,
        Instruction.TLOAD,
        Instruction.TSTORE,
    ];
    public IEnumerable<Instruction> Opcodes => AllValidLegacyOpcodes;

    [ParamsSource(nameof(Opcodes))]
    public Instruction Opcode { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _stackBuffer = CreateStackBuffer();
        _gas = EthereumGasPolicy.FromLong(long.MaxValue);

        // Pre-fill 20 stack slots with unique values for DUP/SWAP tests
        for (int i = 0; i < 20; i++)
        {
            WriteStackSlot(_stackBuffer, i, new UInt256((ulong)(i + 1) * 0x0102030405060708UL));
        }

        // Create VM with opcode table - mirrors VirtualMachine.Warmup pattern
        IReleaseSpec spec = Fork.GetLatest();
        _vm = new BenchmarkVm(new NoOpBlockhashProvider(), MainnetSpecProvider.Instance, LimboLogs.Instance);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);

        Address address = Address.SystemUser;
        _stateProvider.CreateAccount(address, UInt256.One);
        _stateProvider.CreateAccount(CallTargetAddress, UInt256.Zero);
        _stateProvider.InsertCode(CallTargetAddress, StopCode, spec);
        InitializeDynamicStorageLocations(address);
        _stateProvider.Commit(spec);

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);

        BlockHeader header = new(
            Keccak.Zero,
            Keccak.Zero,
            address,
            UInt256.One,
            MainnetSpecProvider.PragueActivation.BlockNumber,
            long.MaxValue,
            1UL,
            [],
            0,
            0);
        _vm.SetBlockExecutionContext(new BlockExecutionContext(header, spec, UInt256.Zero));
        _vm.SetTxExecutionContext(new TxExecutionContext(address, codeInfoRepository, null, 0));
        _vm.SetExecutionDependencies(_stateProvider, codeInfoRepository);

        // Create bytecode buffer for PUSH instructions (64 bytes of data after the opcode)
        byte[] bytecode = new byte[64];
        bytecode[0] = (byte)Instruction.JUMPDEST;
        for (int i = 0; i < bytecode.Length; i++)
        {
            if (i > 0)
            {
                bytecode[i] = (byte)(i + 1);
            }
        }

        _env = ExecutionEnvironment.Rent(
            codeInfo: new CodeInfo(bytecode),
            executingAccount: address,
            caller: address,
            codeSource: address,
            callDepth: 0,
            transferValue: 0,
            value: 0,
            inputData: default);

        _vmState = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromLong(long.MaxValue),
            ExecutionType.TRANSACTION,
            _env,
            new StackAccessTracker(),
            Snapshot.Empty);
        _vmState.InitializeStacks();
        InitializeKeccakMemoryLocations();

        _vm.SetVmState(_vmState);
        _vm.SetTracer(NullTxTracer.Instance);

        // Generate the opcode function pointer table (same as production)
        _opcodes = EvmInstructions.GenerateOpCodes<EthereumGasPolicy, OffFlag>(spec);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _vmState?.Dispose();
        _env?.Dispose();
        _stateScope?.Dispose();
    }

    [Benchmark(OperationsPerInvoke = InnerCount)]
    public EvmExceptionType ExecuteOpcode()
    {
        if (RequiresPerRunLocationSetup(Opcode))
        {
            return ExecuteOpcodeWithPerRunRefresh();
        }

        if (RequiresIndependentBinaryInputs(Opcode))
        {
            return ExecuteOpcodeWithIndependentBinaryInputs();
        }

        return ExecuteOpcodeWithStackWalk();
    }

    private EvmExceptionType ExecuteOpcodeWithStackWalk()
    {
        EvmStack stack = new(_stackDepth, NullTxTracer.Instance, _stackBuffer);
        EvmExceptionType result = EvmExceptionType.None;
        int remaining = InnerCount;
        while (remaining > 0)
        {
            int runs = Math.Min(remaining, _runsPerBatch);
            stack.Head = _stackDepth;

            for (int i = 0; i < runs; i++)
            {
                EthereumGasPolicy gas = _gas;
                int pc = 0;
                result = _opcodes[(int)Opcode](_vm, ref stack, ref gas, ref pc);
                DisposeNestedReturnFrame();
            }

            remaining -= runs;
        }

        return result;
    }

    private EvmExceptionType ExecuteOpcodeWithPerRunRefresh()
    {
        EvmStack stack = new(_stackDepth, NullTxTracer.Instance, _stackBuffer);
        EvmExceptionType result = EvmExceptionType.None;
        for (int runIndex = 0; runIndex < InnerCount; runIndex++)
        {
            stack.Head = _stackDepth;
            PreparePerRunLocationSetup(runIndex);

            EthereumGasPolicy gas = _gas;
            int pc = 0;
            result = _opcodes[(int)Opcode](_vm, ref stack, ref gas, ref pc);
            DisposeNestedReturnFrame();
        }

        return result;
    }

    private EvmExceptionType ExecuteOpcodeWithIndependentBinaryInputs()
    {
        EvmStack stack = new(_stackDepth, NullTxTracer.Instance, _stackBuffer);
        EvmExceptionType result = EvmExceptionType.None;
        int remaining = InnerCount;
        while (remaining > 0)
        {
            int runs = Math.Min(remaining, _runsPerBatch);
            int depth = SetupStackForIndependentBinaryRuns(Opcode, runs);
            for (int i = 0; i < runs; i++)
            {
                stack.Head = depth - (i * 2);

                EthereumGasPolicy gas = _gas;
                int pc = 0;
                result = _opcodes[(int)Opcode](_vm, ref stack, ref gas, ref pc);
                DisposeNestedReturnFrame();
            }

            remaining -= runs;
        }

        return result;
    }

    [IterationSetup]
    public void SetupStackForOpcode()
    {
        _iterationId++;
        bool requiresPerRunRefresh = RequiresPerRunLocationSetup(Opcode);
        if (requiresPerRunRefresh)
        {
            _stackDepth = SetupStackForOpcode(Opcode, runs: 1);
            _runsPerBatch = InnerCount;
            return;
        }

        if (RequiresIndependentBinaryInputs(Opcode))
        {
            _runsPerBatch = Math.Min(InnerCount, EvmStack.MaxStackSize / 2);
            _stackDepth = _runsPerBatch * 2;
            return;
        }

        _stackDepth = SetupStackForOpcode(Opcode, InnerCount);
        _runsPerBatch = CalculateRunsPerBatch(Opcode, _stackDepth, InnerCount);
    }

    internal static bool TryEstimateOpcodeGas(Instruction opcode, out long gas)
    {
        EvmOpcodesBenchmark probe = new() { Opcode = opcode };
        try
        {
            probe.Setup();
            probe.SetupStackForOpcode();
            gas = probe.ExecuteOpcodeOnceForGas();
            return true;
        }
        catch
        {
            gas = 0;
            return false;
        }
        finally
        {
            probe.Cleanup();
        }
    }

    private static int CalculateRunsPerBatch(Instruction opcode, int depth, int requestedRuns)
    {
        if (requestedRuns <= 1)
        {
            return 1;
        }

        (int inputCount, int outputCount) = GetStackIo(opcode);
        int netChange = outputCount - inputCount;
        int maxRuns;

        if (netChange < 0)
        {
            int consumptionPerRun = -netChange;
            if (depth < inputCount)
            {
                return 1;
            }

            maxRuns = ((depth - inputCount) / consumptionPerRun) + 1;
        }
        else if (netChange > 0)
        {
            int maxHead = EvmStack.MaxStackSize - 1;
            int availableGrowth = maxHead - depth;
            if (availableGrowth < 0)
            {
                return 1;
            }

            maxRuns = availableGrowth / netChange;
        }
        else
        {
            maxRuns = requestedRuns;
        }

        return Math.Clamp(maxRuns, 1, requestedRuns);
    }

    private static (int InputCount, int OutputCount) GetStackIo(Instruction opcode)
    {
        return opcode switch
        {
            Instruction.CALL or Instruction.CALLCODE => (7, 1),
            Instruction.DELEGATECALL or Instruction.STATICCALL => (6, 1),
            _ => (opcode.StackRequirements().InputCount, opcode.StackRequirements().OutputCount),
        };
    }

    private int SetupStackForOpcode(Instruction opcode, int runs = 1)
    {
        switch (opcode)
        {
            case Instruction.ADDMOD:
            case Instruction.MULMOD:
                return SetupStackForTernaryRuns(runs, in TernaryOperandA, in TernaryOperandB, in StarkFieldModulus);

            case Instruction.SIGNEXTEND:
                return SetupStackForBinaryRuns(runs, in SignExtendPosition, in ValueA);

            case Instruction.BYTE:
                return SetupStackForBinaryRuns(runs, in BytePosition, in ValueA);

            case Instruction.SHL:
            case Instruction.SHR:
            case Instruction.SAR:
                return SetupStackForBinaryRuns(runs, in ShiftAmount, in ValueA);

            case Instruction.JUMP:
                WriteStackSlot(_stackBuffer, 0, in JumpDestination);
                return 1;

            case Instruction.JUMPI:
                SetupStack2(in JumpDestination, in One);
                return 2;

            case Instruction.CALL:
            case Instruction.CALLCODE:
                SetupCallStack(hasValue: true);
                return 7;

            case Instruction.DELEGATECALL:
            case Instruction.STATICCALL:
                SetupCallStack(hasValue: false);
                return 6;

            case Instruction.EXTCODECOPY:
                return SetupExtCodeCopyStack(runs);

            case Instruction.ADD:
            case Instruction.SUB:
            case Instruction.EXP:
            case Instruction.LT:
            case Instruction.GT:
            case Instruction.SLT:
            case Instruction.SGT:
            case Instruction.EQ:
            case Instruction.AND:
            case Instruction.OR:
            case Instruction.XOR:
                return SetupStackForBinaryRuns(runs);

            case Instruction.MUL:
                return SetupStackForBinaryRuns(runs, in MulOperandA, in MulOperandB);

            case Instruction.DIV:
            case Instruction.SDIV:
            case Instruction.MOD:
            case Instruction.SMOD:
                return SetupStackForBinaryRuns(runs, in DividendOperand, in DivisorOperand);

            default:
                return SetupGenericStack(opcode);
        }
    }

    private int SetupStackForBinaryRuns(int runs, in UInt256 first, in UInt256 second)
    {
        int effectiveRuns = Math.Clamp(runs, 1, EvmStack.MaxStackSize - 1);
        int depth = Math.Max(2, effectiveRuns + 1);
        for (int i = 0; i < depth; i++)
        {
            UInt256 value = (i & 1) == 0 ? second : first;
            WriteStackSlot(_stackBuffer, i, in value);
        }

        int top = depth - 1;
        WriteStackSlot(_stackBuffer, top - 1, in second);
        WriteStackSlot(_stackBuffer, top, in first);
        return depth;
    }

    private int SetupStackForBinaryRuns(int runs)
    {
        return SetupStackForBinaryRuns(runs, in ValueA, in ValueB);
    }

    private int SetupStackForTernaryRuns(int runs, in UInt256 first, in UInt256 second, in UInt256 modulus)
    {
        int effectiveRuns = Math.Clamp(runs, 1, (EvmStack.MaxStackSize - 1) / 2);
        int depth = Math.Max(3, (effectiveRuns * 2) + 1);
        int top = depth - 1;
        WriteStackSlot(_stackBuffer, top, in first);

        // For each chained ternary op run:
        // a = previous result (or initial first), b = constant second, m = constant modulus.
        for (int offset = 1; offset < depth; offset++)
        {
            int slot = top - offset;
            if ((offset & 1) == 1)
            {
                WriteStackSlot(_stackBuffer, slot, in second);
            }
            else
            {
                WriteStackSlot(_stackBuffer, slot, in modulus);
            }
        }

        return depth;
    }

    private int SetupExtCodeCopyStack(int runs)
    {
        int availableSlots = _stackBuffer.Length / EvmStack.WordSize;
        int effectiveRuns = Math.Clamp(runs, 1, availableSlots / 4);
        int depth = Math.Max(4, effectiveRuns * 4);
        for (int run = 0; run < effectiveRuns; run++)
        {
            int slot = run * 4;
            WriteStackSlot(_stackBuffer, slot + 0, UInt256.Zero); // length
            WriteStackSlot(_stackBuffer, slot + 1, UInt256.Zero); // sourceOffset
            WriteStackSlot(_stackBuffer, slot + 2, UInt256.Zero); // memoryOffset
            WriteStackSlot(_stackBuffer, slot + 3, in CallTarget); // address (popped first)
        }

        return depth;
    }

    private int SetupGenericStack(Instruction opcode)
    {
        int inputCount = opcode.StackRequirements().InputCount;
        for (int i = 0; i < inputCount; i++)
        {
            UInt256 value = new((ulong)(i + 1));
            WriteStackSlot(_stackBuffer, i, in value);
        }

        return inputCount;
    }

    private void SetupCallStack(bool hasValue)
    {
        // Stack order from top to bottom:
        // CALL/CALLCODE: gas, addr, value, inOffset, inLength, outOffset, outLength
        // DELEGATECALL/STATICCALL: gas, addr, inOffset, inLength, outOffset, outLength
        WriteStackSlot(_stackBuffer, 0, UInt256.Zero); // outLength
        WriteStackSlot(_stackBuffer, 1, UInt256.Zero); // outOffset
        WriteStackSlot(_stackBuffer, 2, UInt256.Zero); // inLength
        WriteStackSlot(_stackBuffer, 3, UInt256.Zero); // inOffset

        if (hasValue)
        {
            WriteStackSlot(_stackBuffer, 4, UInt256.Zero); // value
            WriteStackSlot(_stackBuffer, 5, in CallTarget); // addr
            WriteStackSlot(_stackBuffer, 6, in CallGasLimit); // gas
        }
        else
        {
            WriteStackSlot(_stackBuffer, 4, in CallTarget); // addr
            WriteStackSlot(_stackBuffer, 5, in CallGasLimit); // gas
        }
    }

    private void SetupStack2(in UInt256 a, in UInt256 b)
    {
        WriteStackSlot(_stackBuffer, 0, in b);
        WriteStackSlot(_stackBuffer, 1, in a);
    }

    private long ExecuteOpcodeOnceForGas()
    {
        EvmStack stack = new(_stackDepth, NullTxTracer.Instance, _stackBuffer);
        if (RequiresPerRunLocationSetup(Opcode))
        {
            stack.Head = _stackDepth;
            PreparePerRunLocationSetup(0);
        }
        else if (RequiresIndependentBinaryInputs(Opcode))
        {
            int depth = SetupStackForIndependentBinaryRuns(Opcode, runs: 1);
            stack.Head = depth;
        }
        else
        {
            stack.Head = _stackDepth;
        }

        EthereumGasPolicy gas = _gas;
        int pc = 0;
        _ = _opcodes[(int)Opcode](_vm, ref stack, ref gas, ref pc);
        DisposeNestedReturnFrame();

        return _gas.Value - gas.Value;
    }

    private void DisposeNestedReturnFrame()
    {
        if (_vm.ReturnData is VmState<EthereumGasPolicy> nestedFrame)
        {
            nestedFrame.Dispose();
            _vm.ReturnData = null!;
        }
    }

    private void SetupStack3(in UInt256 a, in UInt256 b, in UInt256 c)
    {
        WriteStackSlot(_stackBuffer, 0, in c);
        WriteStackSlot(_stackBuffer, 1, in b);
        WriteStackSlot(_stackBuffer, 2, in a);
    }

    private void InitializeDynamicStorageLocations(Address executingAddress)
    {
        _dynamicStorageKeys = new UInt256[InnerCount];
        for (int i = 0; i < InnerCount; i++)
        {
            UInt256 key = DynamicStorageBase + (UInt256)(ulong)i;
            _dynamicStorageKeys[i] = key;

            // Seed locations once so SLOAD/SSTORE rotate over existing slots.
            UInt256 initialValue = (i & 1) == 0 ? ValueA : ValueB;
            _stateProvider.Set(new StorageCell(executingAddress, key), ToStorageBytes(initialValue));
        }
    }

    private void InitializeKeccakMemoryLocations()
    {
        _dynamicKeccakOffsets = new UInt256[InnerCount];
        Span<byte> word = stackalloc byte[KeccakWordSize];
        for (int i = 0; i < InnerCount; i++)
        {
            UInt256 offset = (UInt256)(ulong)(i * KeccakWordSize);
            _dynamicKeccakOffsets[i] = offset;

            for (int b = 0; b < word.Length; b++)
            {
                word[b] = (byte)((i * 131 + b * 17 + 11) & 0xFF);
            }

            _vmState.Memory.TrySave(in offset, word);
        }
    }

    private static byte[] ToStorageBytes(in UInt256 value)
    {
        byte[] bytes = new byte[KeccakWordSize];
        value.ToBigEndian(bytes);
        return bytes;
    }

    private static bool RequiresPerRunLocationSetup(Instruction opcode)
    {
        return PerRunRefreshedOpcodes.Contains(opcode);
    }

    private static bool RequiresIndependentBinaryInputs(Instruction opcode)
    {
        return opcode is Instruction.MUL
            or Instruction.DIV
            or Instruction.SDIV
            or Instruction.MOD
            or Instruction.SMOD;
    }

    private int SetupStackForIndependentBinaryRuns(Instruction opcode, int runs)
    {
        int effectiveRuns = Math.Clamp(runs, 1, EvmStack.MaxStackSize / 2);
        int depth = effectiveRuns * 2;
        GetIndependentBinaryOperands(opcode, out UInt256 first, out UInt256 second);
        for (int run = 0; run < effectiveRuns; run++)
        {
            int head = depth - (run * 2);
            WriteStackSlot(_stackBuffer, head - 1, in first);
            WriteStackSlot(_stackBuffer, head - 2, in second);
        }

        return depth;
    }

    private static void GetIndependentBinaryOperands(Instruction opcode, out UInt256 first, out UInt256 second)
    {
        if (opcode == Instruction.MUL)
        {
            first = MulOperandA;
            second = MulOperandB;
            return;
        }

        first = DividendOperand;
        second = DivisorOperand;
    }

    private void PreparePerRunLocationSetup(int runIndex)
    {
        int index = runIndex % InnerCount;
        UInt256 key = _dynamicStorageKeys[index];

        switch (Opcode)
        {
            case Instruction.KECCAK256:
                UInt256 offset = _dynamicKeccakOffsets[index];
                WriteStackSlot(_stackBuffer, 0, in KeccakLength); // length
                WriteStackSlot(_stackBuffer, 1, in offset); // offset (popped first)
                break;

            case Instruction.SLOAD:
            case Instruction.TLOAD:
                WriteStackSlot(_stackBuffer, 0, in key);
                break;

            case Instruction.SSTORE:
            case Instruction.TSTORE:
                UInt256 value = ((runIndex + _iterationId) & 1) == 0 ? ValueA : ValueB;
                WriteStackSlot(_stackBuffer, 0, in value); // value (popped second)
                WriteStackSlot(_stackBuffer, 1, in key); // key (popped first)
                break;
        }
    }

    /// <summary>
    /// Creates a pinned stack buffer for benchmarking.
    /// </summary>
    public static byte[] CreateStackBuffer()
    {
        // EXTCODECOPY pops 4 values and pushes none; reserve enough slots for InnerCount runs.
        int slots = InnerCount * 4;
        if (slots < EvmStack.MaxStackSize)
        {
            slots = EvmStack.MaxStackSize;
        }

        return GC.AllocateArray<byte>(slots * EvmStack.WordSize, pinned: true);
    }

    /// <summary>
    /// Writes a UInt256 value to stack slot in big-endian format.
    /// </summary>
    public static void WriteStackSlot(byte[] buffer, int slotIndex, in UInt256 value)
    {
        Span<byte> slot = buffer.AsSpan(slotIndex * 32, 32);
        value.ToBigEndian(slot);
    }

    /// <summary>
    /// Subclass to access protected VirtualMachine members for benchmark setup.
    /// </summary>
    private class BenchmarkVm(IBlockhashProvider bhp, ISpecProvider sp, ILogManager lm)
        : VirtualMachine<EthereumGasPolicy>(bhp, sp, lm)
    {
        private static readonly FieldInfo CodeInfoRepositoryField =
            typeof(VirtualMachine<EthereumGasPolicy>)
                .GetField("_codeInfoRepository", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public void SetVmState(VmState<EthereumGasPolicy> state) => VmState = state;
        public void SetTracer(ITxTracer tracer) => _txTracer = tracer;

        public void SetExecutionDependencies(IWorldState state, ICodeInfoRepository codeInfoRepository)
        {
            _worldState = state;
            CodeInfoRepositoryField.SetValue(this, codeInfoRepository);
        }
    }

    private class NoOpBlockhashProvider : IBlockhashProvider
    {
        public Hash256 GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec) => Keccak.Zero;
        public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
    }

    public class EvmOpcodesBenchmarkConfig : ManualConfig
    {
        public EvmOpcodesBenchmarkConfig()
        {
            HideColumns(Column.Method, Column.StdDev, Column.Median);
            AddColumnProvider(new EvmOpcodeGasColumnProvider());
        }
    }
}
