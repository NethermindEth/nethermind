
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls;
using Nethermind.Evm.Precompiles.Snarks;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using static Nethermind.Evm.VirtualMachine;
using static Nethermind.Evm.EvmInstructions;
using static System.Runtime.CompilerServices.Unsafe;
using ValueHash256 = Nethermind.Core.Crypto.ValueHash256;

#if DEBUG
using Nethermind.Evm.Tracing.Debugger;
#endif

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.Evm;

using Int256;

public class VirtualMachine : IVirtualMachine
{
    public const int MaxCallDepth = 1024;
    internal static FrozenDictionary<AddressAsKey, CodeInfo> PrecompileCode { get; } = InitializePrecompiledContracts();
    internal static LruCache<ValueHash256, CodeInfo> CodeCache { get; } = new(MemoryAllowance.CodeCacheSize, MemoryAllowance.CodeCacheSize, "VM bytecodes");

    private readonly static UInt256 P255Int = (UInt256)System.Numerics.BigInteger.Pow(2, 255);
    internal static ref readonly UInt256 P255 => ref P255Int;
    internal static readonly UInt256 BigInt256 = 256;
    internal static readonly UInt256 BigInt32 = 32;

    internal static readonly byte[] BytesZero = { 0 };

    internal static readonly byte[] BytesZero32 =
    {
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0
    };

    internal static readonly byte[] BytesMax32 =
    {
        255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255
    };

    private readonly IVirtualMachine _evm;

    public VirtualMachine(
        IBlockhashProvider? blockhashProvider,
        ISpecProvider? specProvider,
        ILogManager? logManager)
    {
        ILogger logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        if (!logger.IsTrace)
        {
            _evm = new VirtualMachine<NotTracing>(blockhashProvider, specProvider, logger);
        }
        else
        {
            _evm = new VirtualMachine<IsTracing>(blockhashProvider, specProvider, logger);
        }

        WarmUpInstructions(specProvider);
    }

    private unsafe static void WarmUpInstructions(ISpecProvider? specProvider)
    {
        IReleaseSpec spec = specProvider.GetFinalSpec();
        var bytes = new byte[1024];
        EvmStack stack = new(bytes, 0, NullTxTracer.Instance);
        long gasAvailable = long.MaxValue;

        var ops = CalliJmpTable;
        var opNull = ops[0];

        var vmState = new EvmState(gasAvailable, new ExecutionEnvironment { }, ExecutionType.CALL, isTopLevel: true, snapshot: default, isContinuation: false, NullTxTracer.Instance, worldState: null, spec);

        for (var i = 0; i < 40; i++)
        {
            for (var j = 0; j <= (int)Instruction.SAR; j++)
            {
                if ((void*)ops[j] == (void*)opNull)
                {
                    continue;
                }
                AddStack(ref stack);
                ops[j](vmState, ref stack, ref gasAvailable);
            }
        }

        Thread.Sleep(1000);
        CalliJmpTable = CreateInstructLookup();

        static void AddStack(ref EvmStack stack)
        {
            while (stack.PopUInt256(out _)) { }

            Vector256<byte> vect = Vector256<byte>.One;
            stack.PushZero();
            Unsafe.As<byte, Vector256<byte>>(ref stack.PushBytesRef()) = vect;
            stack.PushBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref vect, 1)));
            stack.PushUInt256(in UInt256.MaxValue);
            stack.PushOne();
        }
    }

    public static CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
    {
        if (codeSource.IsPrecompile(vmSpec))
        {
            return PrecompileCode[codeSource];
        }

        CodeInfo cachedCodeInfo = null;
        ValueHash256 codeHash = worldState.GetCodeHash(codeSource);
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256)
        {
            cachedCodeInfo = CodeInfo.Empty;
        }

        cachedCodeInfo ??= CodeCache.Get(codeHash);
        if (cachedCodeInfo is null)
        {
            byte[]? code = worldState.GetCode(codeHash);

            if (code is null)
            {
                MissingCode(codeSource, codeHash);
            }

            cachedCodeInfo = new CodeInfo(code);
            CodeCache.Set(codeHash, cachedCodeInfo);
        }
        else
        {
            Db.Metrics.CodeDbCache++;
            // need to touch code so that any collectors that track database access are informed
            worldState.TouchCode(codeHash);
        }

        return cachedCodeInfo;

        [DoesNotReturn]
        [StackTraceHidden]
        static void MissingCode(Address codeSource, in ValueHash256 codeHash)
        {
            throw new NullReferenceException($"Code {codeHash} missing in the state for address {codeSource}");
        }
    }

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        _evm.InsertCode(code, codeOwner, spec);
    }

    public TransactionSubstate Run<TTracingActions>(EvmState state, IWorldState worldState, ITxTracer txTracer)
        where TTracingActions : struct, VirtualMachine.IIsTracing
        => _evm.Run<TTracingActions>(state, worldState, txTracer);

    private static FrozenDictionary<AddressAsKey, CodeInfo> InitializePrecompiledContracts()
    {
        return new Dictionary<AddressAsKey, CodeInfo>
        {
            [EcRecoverPrecompile.Address] = new(EcRecoverPrecompile.Instance),
            [Sha256Precompile.Address] = new(Sha256Precompile.Instance),
            [Ripemd160Precompile.Address] = new(Ripemd160Precompile.Instance),
            [IdentityPrecompile.Address] = new(IdentityPrecompile.Instance),

            [Bn254AddPrecompile.Address] = new(Bn254AddPrecompile.Instance),
            [Bn254MulPrecompile.Address] = new(Bn254MulPrecompile.Instance),
            [Bn254PairingPrecompile.Address] = new(Bn254PairingPrecompile.Instance),
            [ModExpPrecompile.Address] = new(ModExpPrecompile.Instance),

            [Blake2FPrecompile.Address] = new(Blake2FPrecompile.Instance),

            [G1AddPrecompile.Address] = new(G1AddPrecompile.Instance),
            [G1MulPrecompile.Address] = new(G1MulPrecompile.Instance),
            [G1MultiExpPrecompile.Address] = new(G1MultiExpPrecompile.Instance),
            [G2AddPrecompile.Address] = new(G2AddPrecompile.Instance),
            [G2MulPrecompile.Address] = new(G2MulPrecompile.Instance),
            [G2MultiExpPrecompile.Address] = new(G2MultiExpPrecompile.Instance),
            [PairingPrecompile.Address] = new(PairingPrecompile.Instance),
            [MapToG1Precompile.Address] = new(MapToG1Precompile.Instance),
            [MapToG2Precompile.Address] = new(MapToG2Precompile.Instance),

            [PointEvaluationPrecompile.Address] = new(PointEvaluationPrecompile.Instance),
        }.ToFrozenDictionary();
    }

    internal readonly struct CallResult
    {
        public static CallResult OutOfGasException = new(EvmExceptionType.OutOfGas);
        public static CallResult AccessViolationException = new(EvmExceptionType.AccessViolation);
        public static CallResult InvalidJumpDestination = new(EvmExceptionType.InvalidJumpDestination);
        public static CallResult InvalidInstructionException = new(EvmExceptionType.BadInstruction);
        public static CallResult StaticCallViolationException = new(EvmExceptionType.StaticCallViolation);
        public static CallResult StackOverflowException = new(EvmExceptionType.StackOverflow); // TODO: use these to avoid CALL POP attacks
        public static CallResult StackUnderflowException = new(EvmExceptionType.StackUnderflow); // TODO: use these to avoid CALL POP attacks
        public static CallResult Empty = new(Array.Empty<byte>(), null);

        public CallResult(EvmState stateToExecute)
        {
            StateToExecute = stateToExecute;
            Output = Array.Empty<byte>();
            PrecompileSuccess = null;
            ShouldRevert = false;
            ExceptionType = EvmExceptionType.None;
        }

        private CallResult(EvmExceptionType exceptionType)
        {
            StateToExecute = null;
            Output = StatusCode.FailureBytes;
            PrecompileSuccess = null;
            ShouldRevert = false;
            ExceptionType = exceptionType;
        }

        public CallResult(byte[] output, bool? precompileSuccess, bool shouldRevert = false, EvmExceptionType exceptionType = EvmExceptionType.None)
        {
            StateToExecute = null;
            Output = output;
            PrecompileSuccess = precompileSuccess;
            ShouldRevert = shouldRevert;
            ExceptionType = exceptionType;
        }

        public EvmState? StateToExecute { get; }
        public ReadOnlyMemory<byte> Output { get; }
        public EvmExceptionType ExceptionType { get; }
        public bool ShouldRevert { get; }
        public bool? PrecompileSuccess { get; } // TODO: check this behaviour as it seems it is required and previously that was not the case
        public bool IsReturn => StateToExecute is null;
        public bool IsException => ExceptionType != EvmExceptionType.None;
    }

    public interface IIsTracing
    {
        virtual static bool Tracing
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return false;
            }
        }
    }
    public readonly struct NotTracing : IIsTracing { }
    public readonly struct IsTracing : IIsTracing
    {
        public static bool Tracing
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return true;
            }
        }
    }

    internal static unsafe delegate*<EvmState, ref EvmStack, ref long, EvmExceptionType>[] CalliJmpTable = CreateInstructLookup();
    private static unsafe delegate*<EvmState, ref EvmStack, ref long, EvmExceptionType>[] CreateInstructLookup()
    {
        var lookup = new delegate*<EvmState, ref EvmStack, ref long, EvmExceptionType>[256];
        for (int i = 0; i < lookup.Length; i++)
        {
            lookup[i] = &InstructionBadInstruction;
        }

        lookup[(int)Instruction.ADD] = &InstructionMath2Param<OpAdd>;
        lookup[(int)Instruction.MUL] = &InstructionMath2Param<OpMul>;
        lookup[(int)Instruction.SUB] = &InstructionMath2Param<OpSub>;
        lookup[(int)Instruction.DIV] = &InstructionMath2Param<OpDiv>;
        lookup[(int)Instruction.SDIV] = &InstructionMath2Param<OpSDiv>;
        lookup[(int)Instruction.MOD] = &InstructionMath2Param<OpMod>;
        lookup[(int)Instruction.SMOD] = &InstructionMath2Param<OpSMod>;
        lookup[(int)Instruction.ADDMOD] = &InstructionMath3Param<OpAddMod>;
        lookup[(int)Instruction.MULMOD] = &InstructionMath3Param<OpMulMod>;
        lookup[(int)Instruction.EXP] = &InstructionExp;
        lookup[(int)Instruction.SIGNEXTEND] = &InstructionSignExtend;
        lookup[(int)Instruction.LT] = &InstructionMath2Param<OpLt>;
        lookup[(int)Instruction.GT] = &InstructionMath2Param<OpGt>;
        lookup[(int)Instruction.SLT] = &InstructionMath2Param<OpSLt>;
        lookup[(int)Instruction.SGT] = &InstructionMath2Param<OpSGt>;
        lookup[(int)Instruction.EQ] = &InstructionBitwise<OpBitwiseEq>;
        lookup[(int)Instruction.ISZERO] = &InstructionMath1Param<OpIsZero>;
        lookup[(int)Instruction.AND] = &InstructionBitwise<OpBitwiseAnd>;
        lookup[(int)Instruction.OR] = &InstructionBitwise<OpBitwiseOr>;
        lookup[(int)Instruction.XOR] = &InstructionBitwise<OpBitwiseXor>;
        lookup[(int)Instruction.NOT] = &InstructionMath1Param<OpNot>;
        lookup[(int)Instruction.BYTE] = &InstructionByte;
        lookup[(int)Instruction.SHL] = &InstructionShift<OpShl>;
        lookup[(int)Instruction.SHR] = &InstructionShift<OpShr>;
        lookup[(int)Instruction.SAR] = &InstructionSar;

        lookup[(int)Instruction.KECCAK256] = &InstructionKeccak256;
        lookup[(int)Instruction.ADDRESS] = &InstructionEnvBytes<OpAddress>;
        lookup[(int)Instruction.BALANCE] = &InstructionBalance;
        lookup[(int)Instruction.ORIGIN] = &InstructionEnvBytes<OpOrigin>;
        lookup[(int)Instruction.CALLER] = &InstructionEnvBytes<OpCaller>;
        lookup[(int)Instruction.CALLVALUE] = &InstructionEnvUInt256<OpCallValue>;
        lookup[(int)Instruction.CALLDATALOAD] = &InstructionCallDataLoad;
        lookup[(int)Instruction.CALLDATASIZE] = &InstructionEnvUInt256<OpCallDataSize>;
        lookup[(int)Instruction.CALLDATACOPY] = &InstructionCodeCopy<OpCallDataCopy>;
        lookup[(int)Instruction.CODESIZE] = &InstructionEnvUInt256<OpCodeSize>;
        lookup[(int)Instruction.CODECOPY] = &InstructionCodeCopy<OpCallCopy>;
        lookup[(int)Instruction.CODESIZE] = &InstructionEnvUInt256<OpCodeSize>;
        lookup[(int)Instruction.GASPRICE] = &InstructionEnvUInt256<OpGasPrice>;

        lookup[(int)Instruction.EXTCODECOPY] = &InstructionExtCodeCopy;
        //lookup[(int)Instruction.RETURNDATASIZE] = &InstructionReturnDataSize;
        //lookup[(int)Instruction.RETURNDATACOPY] = &InstructionReturnDataCopy;
        lookup[(int)Instruction.EXTCODEHASH] = &InstructionExtCodeHash;
        //lookup[(int)Instruction.BLOCKHASH] = &InstructionBlockHash;

        lookup[(int)Instruction.COINBASE] = &InstructionEnvBytes<OpCoinbase>;
        lookup[(int)Instruction.TIMESTAMP] = &InstructionEnvUInt256<OpTimestamp>;
        lookup[(int)Instruction.NUMBER] = &InstructionEnvUInt256<OpNumber>;
        lookup[(int)Instruction.PREVRANDAO] = &InstructionPrevRandao;
        lookup[(int)Instruction.GASLIMIT] = &InstructionEnvUInt256<OpGasLimit>;
        lookup[(int)Instruction.CHAINID] = &InstructionChainId;
        lookup[(int)Instruction.SELFBALANCE] = &InstructionSelfBalance;
        lookup[(int)Instruction.BASEFEE] = &InstructionEnvUInt256<OpBaseFee>;
        lookup[(int)Instruction.BLOBHASH] = &InstructionBlobHash;
        lookup[(int)Instruction.BLOBBASEFEE] = &InstructionBlobBaseFee;
        // Gap: 0x4b to 0x4f
        lookup[(int)Instruction.POP] = &InstructionPop;
        lookup[(int)Instruction.MLOAD] = &InstructionMLoad;
        lookup[(int)Instruction.MSTORE] = &InstructionMStore;
        lookup[(int)Instruction.MSTORE8] = &InstructionMStore8;

        return lookup;
    }
}

internal sealed class VirtualMachine<TLogger> : IVirtualMachine where TLogger : struct, IIsTracing
{
    private readonly byte[] _chainId;

    private readonly IBlockhashProvider _blockhashProvider;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;
    private IWorldState _worldState;
    private IWorldState _state;
    private readonly Stack<EvmState> _stateStack = new();
    private (Address Address, bool ShouldDelete) _parityTouchBugAccount = (Address.FromNumber(3), false);
    private ReadOnlyMemory<byte> _returnDataBuffer = Array.Empty<byte>();
    private ITxTracer _txTracer = NullTxTracer.Instance;

    public VirtualMachine(
        IBlockhashProvider? blockhashProvider,
        ISpecProvider? specProvider,
        ILogger logger)
    {
        _logger = logger;
        _blockhashProvider = blockhashProvider ?? throw new ArgumentNullException(nameof(blockhashProvider));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _chainId = ((UInt256)specProvider.ChainId).ToBigEndian();
    }

    public TransactionSubstate Run<TTracingActions>(EvmState state, IWorldState worldState, ITxTracer txTracer)
        where TTracingActions : struct, IIsTracing
    {
        _txTracer = txTracer;
        _state = worldState;
        _worldState = worldState;

        IReleaseSpec spec = _specProvider.GetSpec(state.Env.TxExecutionContext.BlockExecutionContext.Header.Number, state.Env.TxExecutionContext.BlockExecutionContext.Header.Timestamp);
        EvmState currentState = state;
        ReadOnlyMemory<byte>? previousCallResult = null;
        ZeroPaddedSpan previousCallOutput = ZeroPaddedSpan.Empty;
        UInt256 previousCallOutputDestination = UInt256.Zero;
        bool isTracing = _txTracer.IsTracing;

        while (true)
        {
            if (!currentState.IsContinuation)
            {
                _returnDataBuffer = Array.Empty<byte>();
            }

            try
            {
                CallResult callResult;
                if (currentState.IsPrecompile)
                {
                    if (TTracingActions.Tracing)
                    {
                        _txTracer.ReportAction(currentState.GasAvailable, currentState.Env.Value, currentState.From, currentState.To, currentState.Env.InputData, currentState.ExecutionType, true);
                    }

                    callResult = ExecutePrecompile(currentState, spec);

                    if (!callResult.PrecompileSuccess.Value)
                    {
                        if (currentState.IsPrecompile && currentState.IsTopLevel)
                        {
                            Metrics.EvmExceptions++;
                            // TODO: when direct / calls are treated same we should not need such differentiation
                            throw new PrecompileExecutionFailureException();
                        }

                        // TODO: testing it as it seems the way to pass zkSNARKs tests
                        currentState.GasAvailable = 0;
                    }
                }
                else
                {
                    if (TTracingActions.Tracing && !currentState.IsContinuation)
                    {
                        _txTracer.ReportAction(currentState.GasAvailable, currentState.Env.Value, currentState.From, currentState.To, currentState.ExecutionType.IsAnyCreate() ? currentState.Env.CodeInfo.MachineCode : currentState.Env.InputData, currentState.ExecutionType);
                        if (_txTracer.IsTracingCode) _txTracer.ReportByteCode(currentState.Env.CodeInfo.MachineCode);
                    }

                    if (!_txTracer.IsTracingInstructions)
                    {
                        callResult = ExecuteCall<NotTracing>(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination, spec);
                    }
                    else
                    {
                        callResult = ExecuteCall<IsTracing>(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination, spec);
                    }

                    if (!callResult.IsReturn)
                    {
                        _stateStack.Push(currentState);
                        currentState = callResult.StateToExecute;
                        previousCallResult = null; // TODO: testing on ropsten sync, write VirtualMachineTest for this case as it was not covered by Ethereum tests (failing block 9411 on Ropsten https://ropsten.etherscan.io/vmtrace?txhash=0x666194d15c14c54fffafab1a04c08064af165870ef9a87f65711dcce7ed27fe1)
                        _returnDataBuffer = Array.Empty<byte>();
                        previousCallOutput = ZeroPaddedSpan.Empty;
                        continue;
                    }

                    if (callResult.IsException)
                    {
                        if (TTracingActions.Tracing) _txTracer.ReportActionError(callResult.ExceptionType);
                        _worldState.Restore(currentState.Snapshot);

                        RevertParityTouchBugAccount(spec);

                        if (currentState.IsTopLevel)
                        {
                            return new TransactionSubstate(callResult.ExceptionType, isTracing);
                        }

                        previousCallResult = StatusCode.FailureBytes;
                        previousCallOutputDestination = UInt256.Zero;
                        _returnDataBuffer = Array.Empty<byte>();
                        previousCallOutput = ZeroPaddedSpan.Empty;

                        currentState.Dispose();
                        currentState = _stateStack.Pop();
                        currentState.IsContinuation = true;
                        continue;
                    }
                }

                if (currentState.IsTopLevel)
                {
                    if (TTracingActions.Tracing)
                    {
                        long codeDepositGasCost = CodeDepositHandler.CalculateCost(callResult.Output.Length, spec);

                        if (callResult.IsException)
                        {
                            _txTracer.ReportActionError(callResult.ExceptionType);
                        }
                        else if (callResult.ShouldRevert)
                        {
                            _txTracer.ReportActionRevert(currentState.ExecutionType.IsAnyCreate()
                                    ? currentState.GasAvailable - codeDepositGasCost
                                    : currentState.GasAvailable,
                                callResult.Output);
                        }
                        else
                        {
                            if (currentState.ExecutionType.IsAnyCreate() && currentState.GasAvailable < codeDepositGasCost)
                            {
                                if (spec.ChargeForTopLevelCreate)
                                {
                                    _txTracer.ReportActionError(EvmExceptionType.OutOfGas);
                                }
                                else
                                {
                                    _txTracer.ReportActionEnd(currentState.GasAvailable, currentState.To, callResult.Output);
                                }
                            }
                            // Reject code starting with 0xEF if EIP-3541 is enabled.
                            else if (currentState.ExecutionType.IsAnyCreate() && CodeDepositHandler.CodeIsInvalid(spec, callResult.Output))
                            {
                                _txTracer.ReportActionError(EvmExceptionType.InvalidCode);
                            }
                            else
                            {
                                if (currentState.ExecutionType.IsAnyCreate())
                                {
                                    _txTracer.ReportActionEnd(currentState.GasAvailable - codeDepositGasCost, currentState.To, callResult.Output);
                                }
                                else
                                {
                                    _txTracer.ReportActionEnd(currentState.GasAvailable, _returnDataBuffer);
                                }
                            }
                        }
                    }

                    return new TransactionSubstate(
                        callResult.Output,
                        currentState.Refund,
                        (IReadOnlyCollection<Address>)currentState.DestroyList,
                        (IReadOnlyCollection<LogEntry>)currentState.Logs,
                        callResult.ShouldRevert,
                        isTracerConnected: isTracing,
                        _logger);
                }

                Address callCodeOwner = currentState.Env.ExecutingAccount;
                using EvmState previousState = currentState;
                currentState = _stateStack.Pop();
                currentState.IsContinuation = true;
                currentState.GasAvailable += previousState.GasAvailable;
                bool previousStateSucceeded = true;

                if (!callResult.ShouldRevert)
                {
                    long gasAvailableForCodeDeposit = previousState.GasAvailable; // TODO: refactor, this is to fix 61363 Ropsten
                    if (previousState.ExecutionType.IsAnyCreate())
                    {
                        previousCallResult = callCodeOwner.Bytes;
                        previousCallOutputDestination = UInt256.Zero;
                        _returnDataBuffer = Array.Empty<byte>();
                        previousCallOutput = ZeroPaddedSpan.Empty;

                        long codeDepositGasCost = CodeDepositHandler.CalculateCost(callResult.Output.Length, spec);
                        bool invalidCode = CodeDepositHandler.CodeIsInvalid(spec, callResult.Output);
                        if (gasAvailableForCodeDeposit >= codeDepositGasCost && !invalidCode)
                        {
                            ReadOnlyMemory<byte> code = callResult.Output;
                            InsertCode(code, callCodeOwner, spec);

                            currentState.GasAvailable -= codeDepositGasCost;

                            if (TTracingActions.Tracing)
                            {
                                _txTracer.ReportActionEnd(previousState.GasAvailable - codeDepositGasCost, callCodeOwner, callResult.Output);
                            }
                        }
                        else if (spec.FailOnOutOfGasCodeDeposit || invalidCode)
                        {
                            currentState.GasAvailable -= gasAvailableForCodeDeposit;
                            worldState.Restore(previousState.Snapshot);
                            if (!previousState.IsCreateOnPreExistingAccount)
                            {
                                _state.DeleteAccount(callCodeOwner);
                            }

                            previousCallResult = BytesZero;
                            previousStateSucceeded = false;

                            if (TTracingActions.Tracing)
                            {
                                _txTracer.ReportActionError(invalidCode ? EvmExceptionType.InvalidCode : EvmExceptionType.OutOfGas);
                            }
                        }
                        else if (TTracingActions.Tracing)
                        {
                            _txTracer.ReportActionEnd(0L, callCodeOwner, callResult.Output);
                        }
                    }
                    else
                    {
                        _returnDataBuffer = callResult.Output;
                        previousCallResult = callResult.PrecompileSuccess.HasValue ? (callResult.PrecompileSuccess.Value ? StatusCode.SuccessBytes : StatusCode.FailureBytes) : StatusCode.SuccessBytes;
                        previousCallOutput = callResult.Output.Span.SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                        previousCallOutputDestination = (ulong)previousState.OutputDestination;
                        if (previousState.IsPrecompile)
                        {
                            // parity induced if else for vmtrace
                            if (_txTracer.IsTracingInstructions)
                            {
                                _txTracer.ReportMemoryChange((long)previousCallOutputDestination, previousCallOutput);
                            }
                        }

                        if (TTracingActions.Tracing)
                        {
                            _txTracer.ReportActionEnd(previousState.GasAvailable, _returnDataBuffer);
                        }
                    }

                    if (previousStateSucceeded)
                    {
                        previousState.CommitToParent(currentState);
                    }
                }
                else
                {
                    worldState.Restore(previousState.Snapshot);
                    _returnDataBuffer = callResult.Output;
                    previousCallResult = StatusCode.FailureBytes;
                    previousCallOutput = callResult.Output.Span.SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                    previousCallOutputDestination = (ulong)previousState.OutputDestination;


                    if (TTracingActions.Tracing)
                    {
                        _txTracer.ReportActionRevert(previousState.GasAvailable, callResult.Output);
                    }
                }
            }
            catch (Exception ex) when (ex is EvmException or OverflowException)
            {
                if (TLogger.Tracing) _logger.Trace($"exception ({ex.GetType().Name}) in {currentState.ExecutionType} at depth {currentState.Env.CallDepth} - restoring snapshot");

                _worldState.Restore(currentState.Snapshot);

                RevertParityTouchBugAccount(spec);

                if (txTracer.IsTracingInstructions)
                {
                    txTracer.ReportOperationRemainingGas(0);
                    txTracer.ReportOperationError(ex is EvmException evmException ? evmException.ExceptionType : EvmExceptionType.Other);
                }

                if (TTracingActions.Tracing)
                {
                    EvmException evmException = ex as EvmException;
                    _txTracer.ReportActionError(evmException?.ExceptionType ?? EvmExceptionType.Other);
                }

                if (currentState.IsTopLevel)
                {
                    return new TransactionSubstate(ex is OverflowException ? EvmExceptionType.Other : (ex as EvmException).ExceptionType, isTracing);
                }

                previousCallResult = StatusCode.FailureBytes;
                previousCallOutputDestination = UInt256.Zero;
                _returnDataBuffer = Array.Empty<byte>();
                previousCallOutput = ZeroPaddedSpan.Empty;

                currentState.Dispose();
                currentState = _stateStack.Pop();
                currentState.IsContinuation = true;
            }
        }
    }

    public void InsertCode(ReadOnlyMemory<byte> code, Address callCodeOwner, IReleaseSpec spec)
    {
        var codeInfo = new CodeInfo(code);
        codeInfo.AnalyseInBackgroundIfRequired();

        Hash256 codeHash = code.Length == 0 ? Keccak.OfAnEmptyString : Keccak.Compute(code.Span);
        _state.InsertCode(callCodeOwner, codeHash, code, spec);
        CodeCache.Set(codeHash, codeInfo);
    }

    private void RevertParityTouchBugAccount(IReleaseSpec spec)
    {
        if (_parityTouchBugAccount.ShouldDelete)
        {
            if (_state.AccountExists(_parityTouchBugAccount.Address))
            {
                _state.AddToBalance(_parityTouchBugAccount.Address, UInt256.Zero, spec);
            }

            _parityTouchBugAccount.ShouldDelete = false;
        }
    }

    private enum StorageAccessType
    {
        SLOAD,
        SSTORE
    }

    private bool ChargeStorageAccessGas(
        ref long gasAvailable,
        EvmState vmState,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec)
    {
        // Console.WriteLine($"Accessing {storageCell} {storageAccessType}");

        bool result = true;
        if (spec.UseHotAndColdStorage)
        {
            if (_txTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
            {
                vmState.WarmUp(in storageCell);
            }

            if (vmState.IsCold(in storageCell))
            {
                result = UpdateGas(GasCostOf.ColdSLoad, ref gasAvailable);
                vmState.WarmUp(in storageCell);
            }
            else if (storageAccessType == StorageAccessType.SLOAD)
            {
                // we do not charge for WARM_STORAGE_READ_COST in SSTORE scenario
                result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
            }
        }

        return result;
    }

    private CallResult ExecutePrecompile(EvmState state, IReleaseSpec spec)
    {
        ReadOnlyMemory<byte> callData = state.Env.InputData;
        UInt256 transferValue = state.Env.TransferValue;
        long gasAvailable = state.GasAvailable;

        IPrecompile precompile = state.Env.CodeInfo.Precompile;
        long baseGasCost = precompile.BaseGasCost(spec);
        long blobGasCost = precompile.DataGasCost(callData, spec);

        bool wasCreated = false;
        if (!_state.AccountExists(state.Env.ExecutingAccount))
        {
            wasCreated = true;
            _state.CreateAccount(state.Env.ExecutingAccount, transferValue);
        }
        else
        {
            _state.AddToBalance(state.Env.ExecutingAccount, transferValue, spec);
        }

        // https://github.com/ethereum/EIPs/blob/master/EIPS/eip-161.md
        // An additional issue was found in Parity,
        // where the Parity client incorrectly failed
        // to revert empty account deletions in a more limited set of contexts
        // involving out-of-gas calls to precompiled contracts;
        // the new Geth behavior matches Parity’s,
        // and empty accounts will cease to be a source of concern in general
        // in about one week once the state clearing process finishes.
        if (state.Env.ExecutingAccount.Equals(_parityTouchBugAccount.Address)
            && !wasCreated
            && transferValue.IsZero
            && spec.ClearEmptyAccountWhenTouched)
        {
            _parityTouchBugAccount.ShouldDelete = true;
        }

        if (!UpdateGas(checked(baseGasCost + blobGasCost), ref gasAvailable))
        {
            Metrics.EvmExceptions++;
            throw new OutOfGasException();
        }

        state.GasAvailable = gasAvailable;

        try
        {
            (ReadOnlyMemory<byte> output, bool success) = precompile.Run(callData, spec);
            CallResult callResult = new(output.ToArray(), success, !success);
            return callResult;
        }
        catch (DllNotFoundException exception)
        {
            if (_logger.IsError) _logger.Error($"Failed to load one of the dependencies of {precompile.GetType()} precompile", exception);
            throw;
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Precompiled contract ({precompile.GetType()}) execution exception", exception);
            CallResult callResult = new(Array.Empty<byte>(), false, true);
            return callResult;
        }
    }

    /// <remarks>
    /// Struct generic parameter is used to burn out all the if statements and inner code
    /// by typeof(TTracingInstructions) == typeof(NotTracing) checks that are evaluated to constant
    /// values at compile time.
    /// </remarks>
    [SkipLocalsInit]
    private CallResult ExecuteCall<TTracingInstructions>(EvmState vmState, ReadOnlyMemory<byte>? previousCallResult, ZeroPaddedSpan previousCallOutput, scoped in UInt256 previousCallOutputDestination, IReleaseSpec spec)
        where TTracingInstructions : struct, IIsTracing
    {
        ref readonly ExecutionEnvironment env = ref vmState.Env;
        if (!vmState.IsContinuation)
        {
            if (!_state.AccountExists(env.ExecutingAccount))
            {
                _state.CreateAccount(env.ExecutingAccount, env.TransferValue);
            }
            else
            {
                _state.AddToBalance(env.ExecutingAccount, env.TransferValue, spec);
            }

            if (vmState.ExecutionType.IsAnyCreate() && spec.ClearEmptyAccountWhenTouched)
            {
                _state.IncrementNonce(env.ExecutingAccount);
            }
        }

        if (env.CodeInfo.MachineCode.Length == 0)
        {
            if (!vmState.IsTopLevel)
            {
                Metrics.EmptyCalls++;
            }
            goto Empty;
        }

        vmState.InitStacks();
        EvmStack stack = new(vmState.DataStack.AsSpan(), vmState.DataStackHead, _txTracer);
        long gasAvailable = vmState.GasAvailable;

        if (previousCallResult is not null)
        {
            stack.PushBytes(previousCallResult.Value.Span);
            if (TTracingInstructions.Tracing) _txTracer.ReportOperationRemainingGas(vmState.GasAvailable);
        }

        if (previousCallOutput.Length > 0)
        {
            UInt256 localPreviousDest = previousCallOutputDestination;
            if (!UpdateMemoryCost(vmState, ref gasAvailable, in localPreviousDest, (ulong)previousCallOutput.Length))
            {
                goto OutOfGas;
            }

            vmState.Memory.Save(in localPreviousDest, previousCallOutput);
        }

        // Struct generic parameter is used to burn out all the if statements
        // and inner code by typeof(TTracing) == typeof(NotTracing)
        // checks that are evaluated to constant values at compile time.
        // This only works for structs, not for classes or interface types
        // which use shared generics.
        if (!_txTracer.IsTracingRefunds)
        {
            return _txTracer.IsTracingOpLevelStorage ?
                ExecuteCode<TTracingInstructions, NotTracing, IsTracing>(vmState, ref stack, gasAvailable) :
                ExecuteCode<TTracingInstructions, NotTracing, NotTracing>(vmState, ref stack, gasAvailable);
        }
        else
        {
            return _txTracer.IsTracingOpLevelStorage ?
                ExecuteCode<TTracingInstructions, IsTracing, IsTracing>(vmState, ref stack, gasAvailable) :
                ExecuteCode<TTracingInstructions, IsTracing, NotTracing>(vmState, ref stack, gasAvailable);
        }
    Empty:
        return CallResult.Empty;
    OutOfGas:
        return CallResult.OutOfGasException;
    }

    [SkipLocalsInit]
    private unsafe CallResult ExecuteCode<TTracingInstructions, TTracingRefunds, TTracingStorage>(EvmState vmState, scoped ref EvmStack stack, long gasAvailable)
        where TTracingInstructions : struct, IIsTracing
        where TTracingRefunds : struct, IIsTracing
        where TTracingStorage : struct, IIsTracing
    {
        bool isCancelable = _txTracer.IsCancelable;
        bool isRevert = false;
        EvmExceptionType exceptionType = EvmExceptionType.None;
        object returnData;

        ReadOnlySpan<byte> code = vmState.Env.CodeInfo.MachineCode.Span;
        var codeLength = code.Length;
        ref byte codeStart = ref MemoryMarshal.GetReference(code);

#if DEBUG
        DebugTracer? debugger = _txTracer.GetTracer<DebugTracer>();
#endif
        nint currentOffset = -1;
        ulong codeSegment = 0;
        int programCounter = vmState.ProgramCounter;
        while ((uint)programCounter < (uint)codeLength)
        {
            if (isCancelable && _txTracer.IsCancelled)
            {
                ThrowOperationCanceledException();
            }

            Instruction instruction;
            if (programCounter <= codeLength - sizeof(ulong))
            {
                // `programCounter >> 3` is equivalent to `programCounter / 8`
                nint offset = programCounter >> 3;
                if (currentOffset != offset)
                {
                    // Read code in 8 byte chunks from memory
                    codeSegment = ReadUnaligned<ulong>(ref Add(ref codeStart, (nint)offset << 3));
                    currentOffset = offset;
                }
                // extract instruction from ulong
                // `(byte)programCounter << 3` is equivalent to `programCounter % 8 * 8`
                instruction = (Instruction)(byte)(codeSegment >> (((int)(byte)programCounter) << 3));
            }
            else
            {
                // Read 1 byte of code from memory
                instruction = (Instruction)Add(ref codeStart, (uint)programCounter);
            }
#if DEBUG
            debugger?.TryWait(ref vmState, ref programCounter, ref gasAvailable, ref stack.Head);
#endif
            // Evaluated to constant at compile time and code elided if not tracing
            if (TTracingInstructions.Tracing)
                StartInstructionTrace(instruction, vmState, gasAvailable, programCounter, in stack);

            programCounter++;
            exceptionType = EvmExceptionType.None;
            if (instruction == Instruction.STOP)
            {
                goto EmptyReturn;
            }
            if (instruction <= Instruction.GASPRICE || (instruction >= Instruction.COINBASE && instruction < Instruction.POP))
            {
                exceptionType = CalliJmpTable[(int)instruction](vmState, ref stack, ref gasAvailable);
                goto Next;
            }
            else
            {
                switch (instruction)
                {
                    // Instruction.STOP
                    // ...
                    // Instruction.GASPRICE
                    case Instruction.EXTCODESIZE:
                        if (!TTracingInstructions.Tracing && programCounter < codeLength)
                        {
                            exceptionType = InstructionExtCodeSizeOptimized(vmState, ref stack, ref gasAvailable, ref programCounter, (Instruction)Add(ref codeStart, (uint)programCounter));
                        }
                        else
                        {
                            exceptionType = InstructionExtCodeSizeTracing(vmState, ref stack, ref gasAvailable);
                        }
                        break;
                    case Instruction.EXTCODECOPY:
                        exceptionType = InstructionExtCodeCopy(vmState, ref stack, ref gasAvailable);
                        break;
                    case Instruction.RETURNDATASIZE:
                        exceptionType = InstructionReturnDataSize(ref stack, ref gasAvailable, vmState.Spec);
                        break;
                    case Instruction.RETURNDATACOPY:
                        exceptionType = InstructionReturnDataCopy(vmState, ref stack, ref gasAvailable);
                        break;
                    case Instruction.EXTCODEHASH:
                        exceptionType = InstructionExtCodeHash(vmState, ref stack, ref gasAvailable);
                        break;
                    case Instruction.BLOCKHASH:
                        exceptionType = InstructionBlockHash(vmState, ref stack, ref gasAvailable);
                        break;
                    // Instruction.COINBASE
                    // ...
                    // Instruction.MSTORE8
                    case Instruction.SLOAD:
                        exceptionType = InstructionSLoad<TTracingInstructions, TTracingStorage>(vmState, ref stack, ref gasAvailable);
                        break;
                    case Instruction.SSTORE:
                        exceptionType = InstructionSStore<TTracingInstructions, TTracingRefunds, TTracingStorage>(vmState, ref stack, ref gasAvailable);
                        break;
                    case Instruction.JUMP:
                        exceptionType = InstructionJump(vmState, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.JUMPI:
                        exceptionType = InstructionJumpIf(vmState, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.PC:
                        gasAvailable -= GasCostOf.Base;
                        stack.PushUInt32(programCounter - 1);
                        break;
                    case Instruction.MSIZE:
                        InstructionEnvUInt256<OpMSize>(vmState, ref stack, ref gasAvailable);
                        break;
                    case Instruction.GAS:
                        exceptionType = InstructionGas(ref stack, ref gasAvailable);
                        break;
                    case Instruction.JUMPDEST:
                        gasAvailable -= GasCostOf.JumpDest;
                        break;
                    case Instruction.TLOAD:
                        exceptionType = InstructionTLoad<TTracingInstructions, TTracingStorage>(vmState, ref stack, ref gasAvailable);
                        break;
                    case Instruction.TSTORE:
                        exceptionType = InstructionTStore<TTracingInstructions, TTracingStorage>(vmState, ref stack, ref gasAvailable);
                        break;
                    case Instruction.MCOPY:
                        exceptionType = InstructionMCopy(vmState, ref stack, ref gasAvailable);
                        break;
                    case Instruction.PUSH0:
                        if (!vmState.Spec.IncludePush0Instruction) goto InvalidInstruction;
                        gasAvailable -= GasCostOf.Base;
                        stack.PushZero();
                        break;
                    case Instruction.PUSH1:
                        gasAvailable -= GasCostOf.VeryLow;
                        if ((uint)programCounter >= (uint)codeLength)
                        {
                            stack.PushZero();
                        }
                        else
                        {
                            stack.PushByte(Add(ref codeStart, (uint)programCounter));
                        }

                        programCounter++;
                        break;
                    case Instruction.PUSH2:
                    case Instruction.PUSH3:
                    case Instruction.PUSH4:
                    case Instruction.PUSH5:
                    case Instruction.PUSH6:
                    case Instruction.PUSH7:
                    case Instruction.PUSH8:
                    case Instruction.PUSH9:
                    case Instruction.PUSH10:
                    case Instruction.PUSH11:
                    case Instruction.PUSH12:
                    case Instruction.PUSH13:
                    case Instruction.PUSH14:
                    case Instruction.PUSH15:
                    case Instruction.PUSH16:
                    case Instruction.PUSH17:
                    case Instruction.PUSH18:
                    case Instruction.PUSH19:
                    case Instruction.PUSH20:
                    case Instruction.PUSH21:
                    case Instruction.PUSH22:
                    case Instruction.PUSH23:
                    case Instruction.PUSH24:
                    case Instruction.PUSH25:
                    case Instruction.PUSH26:
                    case Instruction.PUSH27:
                    case Instruction.PUSH28:
                    case Instruction.PUSH29:
                    case Instruction.PUSH30:
                    case Instruction.PUSH31:
                    case Instruction.PUSH32:
                        InstructionPushN(ref stack, ref gasAvailable, ref programCounter, instruction, code);
                        break;
                    case Instruction.DUP1:
                    case Instruction.DUP2:
                    case Instruction.DUP3:
                    case Instruction.DUP4:
                    case Instruction.DUP5:
                    case Instruction.DUP6:
                    case Instruction.DUP7:
                    case Instruction.DUP8:
                    case Instruction.DUP9:
                    case Instruction.DUP10:
                    case Instruction.DUP11:
                    case Instruction.DUP12:
                    case Instruction.DUP13:
                    case Instruction.DUP14:
                    case Instruction.DUP15:
                    case Instruction.DUP16:
                        gasAvailable -= GasCostOf.VeryLow;
                        if (!stack.Dup(instruction - Instruction.DUP1 + 1)) goto StackUnderflow;
                        break;
                    case Instruction.SWAP1:
                    case Instruction.SWAP2:
                    case Instruction.SWAP3:
                    case Instruction.SWAP4:
                    case Instruction.SWAP5:
                    case Instruction.SWAP6:
                    case Instruction.SWAP7:
                    case Instruction.SWAP8:
                    case Instruction.SWAP9:
                    case Instruction.SWAP10:
                    case Instruction.SWAP11:
                    case Instruction.SWAP12:
                    case Instruction.SWAP13:
                    case Instruction.SWAP14:
                    case Instruction.SWAP15:
                    case Instruction.SWAP16:
                        gasAvailable -= GasCostOf.VeryLow;
                        if (!stack.Swap(instruction - Instruction.SWAP1 + 2)) goto StackUnderflow;
                        break;
                    case Instruction.LOG0:
                    case Instruction.LOG1:
                    case Instruction.LOG2:
                    case Instruction.LOG3:
                    case Instruction.LOG4:
                        exceptionType = InstructionLog(vmState, ref stack, ref gasAvailable, instruction);
                        break;
                    // Gap: 0xa5 to 0xef
                    case Instruction.CREATE:
                    case Instruction.CREATE2:
                        (exceptionType, returnData) = InstructionCreate(vmState, ref stack, ref gasAvailable, instruction);
                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;
                        if (returnData is not null) goto DataReturnNoTrace;
                        break;
                    case Instruction.RETURN:
                        exceptionType = InstructionReturn(vmState, ref stack, ref gasAvailable, out returnData);
                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;
                        goto DataReturn;
                    case Instruction.CALL:
                    case Instruction.CALLCODE:
                    case Instruction.DELEGATECALL:
                    case Instruction.STATICCALL:
                        exceptionType = InstructionCall<TTracingInstructions, TTracingRefunds>(vmState, ref stack, ref gasAvailable, instruction, out returnData);
                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;
                        if (returnData is null)
                        {
                            break;
                        }
                        goto DataReturn;
                    // Gap: 0xfc
                    case Instruction.REVERT:
                        exceptionType = InstructionRevert(vmState, ref stack, ref gasAvailable, out returnData);
                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;
                        goto DataRevert;
                    case Instruction.INVALID:
                        gasAvailable -= GasCostOf.High;
                        goto InvalidInstruction;
                    case Instruction.SELFDESTRUCT:
                        exceptionType = InstructionSelfDestruct(vmState, ref stack, ref gasAvailable);
                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;
                        goto EmptyReturn;
                    default:
                        goto InvalidInstruction;
                }
            }

        Next:

            if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

            if (gasAvailable < 0)
            {
                goto OutOfGas;
            }

            if (TTracingInstructions.Tracing)
            {
                EndInstructionTrace(gasAvailable, vmState.Memory.Size);
            }
        }

        goto EmptyReturnNoTrace;
    // Common exit errors, goto labels to reduce in loop code duplication and to keep loop body smaller
    EmptyReturn:
        if (TTracingInstructions.Tracing) EndInstructionTrace(gasAvailable, vmState.Memory.Size);
        EmptyReturnNoTrace:
        // Ensure gas is positive before updating state
        if (gasAvailable < 0) goto OutOfGas;
        UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
#if DEBUG
        debugger?.TryWait(ref vmState, ref programCounter, ref gasAvailable, ref stack.Head);
#endif
        return CallResult.Empty;
    DataRevert:
        isRevert = true;
    DataReturn:
        if (TTracingInstructions.Tracing) EndInstructionTrace(gasAvailable, vmState.Memory.Size);
        DataReturnNoTrace:
        // Ensure gas is positive before updating state
        if (gasAvailable < 0) goto OutOfGas;
        UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);

        if (returnData is EvmState state)
        {
            return new CallResult(state);
        }
        return new CallResult((byte[])returnData, null, shouldRevert: isRevert);

    OutOfGas:
        exceptionType = EvmExceptionType.OutOfGas;
        goto ReturnFailure;
    InvalidInstruction:
        exceptionType = EvmExceptionType.BadInstruction;
        goto ReturnFailure;
    StackUnderflow:
        exceptionType = EvmExceptionType.StackUnderflow;
        goto ReturnFailure;
    ReturnFailure:
        return GetFailureReturn<TTracingInstructions>(gasAvailable, exceptionType);

        [DoesNotReturn]
        static void ThrowOperationCanceledException() =>
            throw new OperationCanceledException("Cancellation Requested");
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public EvmExceptionType InstructionReturnDataSize(ref EvmStack stack, ref long gasAvailable, IReleaseSpec spec)
    {
        if (!spec.ReturnDataOpcodesEnabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= GasCostOf.Base;

        stack.PushUInt32(_returnDataBuffer.Length);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public EvmExceptionType InstructionTLoad<TTracingInstructions, TTracingStorage>(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
        where TTracingInstructions : struct, IIsTracing
        where TTracingStorage : struct, IIsTracing
    {
        IReleaseSpec spec = vmState.Spec;
        if (!spec.TransientStorageEnabled) return EvmExceptionType.BadInstruction;

        Metrics.TloadOpcode++;
        gasAvailable -= GasCostOf.TLoad;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);

        ReadOnlySpan<byte> value = _state.GetTransientState(in storageCell);
        stack.PushBytes(value);

        if (TTracingStorage.Tracing)
        {
            if (gasAvailable < 0) return EvmExceptionType.OutOfGas;
            _txTracer.LoadOperationTransientStorage(storageCell.Address, result, value);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public EvmExceptionType InstructionTStore<TTracingInstructions, TTracingStorage>(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
        where TTracingInstructions : struct, IIsTracing
        where TTracingStorage : struct, IIsTracing
    {
        IReleaseSpec spec = vmState.Spec;
        if (!spec.TransientStorageEnabled) return EvmExceptionType.BadInstruction;

        Metrics.TstoreOpcode++;

        if (vmState.IsStatic) return EvmExceptionType.StaticCallViolation;

        gasAvailable -= GasCostOf.TStore;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);
        Span<byte> bytes = stack.PopWord256();
        _state.SetTransientState(in storageCell, !bytes.IsZero() ? bytes.ToArray() : BytesZero32);
        if (TTracingStorage.Tracing)
        {
            if (gasAvailable < 0) return EvmExceptionType.OutOfGas;
            ReadOnlySpan<byte> currentValue = _state.GetTransientState(in storageCell);
            _txTracer.SetOperationTransientStorage(storageCell.Address, result, bytes, currentValue);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private EvmExceptionType InstructionExtCodeSizeOptimized(EvmState vmState, ref EvmStack stack, ref long gasAvailable, ref int programCounter, Instruction nextInstruction)
    {
        IReleaseSpec spec = vmState.Spec;
        gasAvailable -= spec.GetExtCodeCost();

        Address address = stack.PopAddress();
        if (address is null) return EvmExceptionType.StackUnderflow;

        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) return EvmExceptionType.OutOfGas;

        bool optimizeAccess = false;
        // code.length is zero
        if (nextInstruction == Instruction.ISZERO)
        {
            optimizeAccess = true;
        }
        // code.length > 0 || code.length == 0
        else if ((nextInstruction == Instruction.GT || nextInstruction == Instruction.EQ) &&
                stack.PeekUInt256IsZero())
        {
            optimizeAccess = true;
            stack.PopLimbo();
        }

        if (optimizeAccess)
        {
            // EXTCODESIZE ISZERO/GT/EQ peephole optimization.
            // In solidity 0.8.1+: `return account.code.length > 0;`
            // is is a common pattern to check if address is a contract
            // however we can just check the address's loaded CodeHash
            // to reduce storage access from trying to load the code

            programCounter++;
            // Add gas cost for ISZERO, GT, or EQ
            gasAvailable -= GasCostOf.VeryLow;

            // IsContract
            bool isCodeLengthNotZero = _state.IsContract(address);
            if (nextInstruction == Instruction.GT)
            {
                // Invert, to IsNotContract
                isCodeLengthNotZero = !isCodeLengthNotZero;
            }

            if (!isCodeLengthNotZero)
            {
                stack.PushOne();
            }
            else
            {
                stack.PushZero();
            }

            return EvmExceptionType.None;
        }

        return OpExtCodeSize(address, ref stack, spec);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private EvmExceptionType InstructionExtCodeSizeTracing(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        IReleaseSpec spec = vmState.Spec;
        gasAvailable -= spec.GetExtCodeCost();

        Address address = stack.PopAddress();
        if (address is null) return EvmExceptionType.StackUnderflow;

        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) return EvmExceptionType.OutOfGas;

        return OpExtCodeSize(address, ref stack, spec);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private EvmExceptionType OpExtCodeSize(Address address, ref EvmStack stack, IReleaseSpec spec)
    {
        int codeLength = GetCachedCodeInfo(_worldState, address, spec).MachineCode.Length;
        stack.PushUInt32(codeLength);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public EvmExceptionType InstructionBlockHash(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        Metrics.BlockhashOpcode++;

        gasAvailable -= GasCostOf.BlockHash;

        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        long number = a > long.MaxValue ? long.MaxValue : (long)a;
        Hash256 blockHash = _blockhashProvider.GetBlockhash(vmState.Env.TxExecutionContext.BlockExecutionContext.Header, number);
        stack.PushBytes(blockHash is not null ? blockHash.Bytes : BytesZero32);

        if (TLogger.Tracing)
        {
            if (_txTracer.IsTracingBlockHash && blockHash is not null)
            {
                _txTracer.ReportBlockHash(blockHash);
            }
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public EvmExceptionType InstructionMCopy(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        IReleaseSpec spec = vmState.Spec;
        if (!spec.MCopyIncluded) return EvmExceptionType.BadInstruction;

        Metrics.MCopyOpcode++;

        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 c)) return EvmExceptionType.StackUnderflow;

        gasAvailable -= GasCostOf.VeryLow + GasCostOf.VeryLow * EvmPooledMemory.Div32Ceiling(c);
        if (!UpdateMemoryCost(vmState, ref gasAvailable, UInt256.Max(b, a), c)) return EvmExceptionType.OutOfGas;

        Span<byte> bytes = vmState.Memory.LoadSpan(in b, c);

        var isTracingInstructions = _txTracer.IsTracingInstructions;
        if (isTracingInstructions) _txTracer.ReportMemoryChange(b, bytes);
        vmState.Memory.Save(in a, bytes);
        if (isTracingInstructions) _txTracer.ReportMemoryChange(a, bytes);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public EvmExceptionType InstructionReturnDataCopy(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        IReleaseSpec spec = vmState.Spec;
        if (!spec.ReturnDataOpcodesEnabled) return EvmExceptionType.BadInstruction;

        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result);

        if (UInt256.AddOverflow(result, b, out var c) || c > _returnDataBuffer.Length)
        {
            return EvmExceptionType.AccessViolation;
        }

        if (!result.IsZero)
        {
            if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, result)) return EvmExceptionType.OutOfGas;

            ZeroPaddedSpan slice = _returnDataBuffer.Span.SliceWithZeroPadding(b, (int)result);
            vmState.Memory.Save(in a, in slice);
            if (_txTracer.IsTracingInstructions)
            {
                _txTracer.ReportMemoryChange((long)a, in slice);
            }
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private EvmExceptionType InstructionCall<TTracingInstructions, TTracingRefunds>(EvmState vmState, ref EvmStack stack, ref long gasAvailable, Instruction instruction, out object returnData)
        where TTracingInstructions : struct, IIsTracing
        where TTracingRefunds : struct, IIsTracing
    {
        returnData = null;
        ref readonly ExecutionEnvironment env = ref vmState.Env;

        Metrics.Calls++;

        IReleaseSpec spec = vmState.Spec;
        if (instruction == Instruction.DELEGATECALL && !spec.DelegateCallEnabled ||
            instruction == Instruction.STATICCALL && !spec.StaticCallEnabled) return EvmExceptionType.BadInstruction;

        if (!stack.PopUInt256(out UInt256 gasLimit)) return EvmExceptionType.StackUnderflow;
        Address codeSource = stack.PopAddress();
        if (codeSource is null) return EvmExceptionType.StackUnderflow;

        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, codeSource, spec)) return EvmExceptionType.OutOfGas;

        UInt256 callValue;
        switch (instruction)
        {
            case Instruction.STATICCALL:
                callValue = UInt256.Zero;
                break;
            case Instruction.DELEGATECALL:
                callValue = env.Value;
                break;
            default:
                if (!stack.PopUInt256(out callValue)) return EvmExceptionType.StackUnderflow;
                break;
        }

        UInt256 transferValue = instruction == Instruction.DELEGATECALL ? UInt256.Zero : callValue;
        if (!stack.PopUInt256(out UInt256 dataOffset)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 dataLength)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 outputOffset)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 outputLength)) return EvmExceptionType.StackUnderflow;

        if (vmState.IsStatic && !transferValue.IsZero && instruction != Instruction.CALLCODE) return EvmExceptionType.StaticCallViolation;

        Address caller = instruction == Instruction.DELEGATECALL ? env.Caller : env.ExecutingAccount;
        Address target = instruction == Instruction.CALL || instruction == Instruction.STATICCALL
            ? codeSource
            : env.ExecutingAccount;

        if (TLogger.Tracing)
        {
            _logger.Trace($"caller {caller}");
            _logger.Trace($"code source {codeSource}");
            _logger.Trace($"target {target}");
            _logger.Trace($"value {callValue}");
            _logger.Trace($"transfer value {transferValue}");
        }

        long gasExtra = 0L;

        if (!transferValue.IsZero)
        {
            gasExtra += GasCostOf.CallValue;
        }

        if (!spec.ClearEmptyAccountWhenTouched && !_state.AccountExists(target))
        {
            gasExtra += GasCostOf.NewAccount;
        }
        else if (spec.ClearEmptyAccountWhenTouched && transferValue != 0 && _state.IsDeadAccount(target))
        {
            gasExtra += GasCostOf.NewAccount;
        }

        if (!UpdateGas(spec.GetCallCost(), ref gasAvailable) ||
            !UpdateMemoryCost(vmState, ref gasAvailable, in dataOffset, dataLength) ||
            !UpdateMemoryCost(vmState, ref gasAvailable, in outputOffset, outputLength) ||
            !UpdateGas(gasExtra, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        if (spec.Use63Over64Rule)
        {
            gasLimit = UInt256.Min((UInt256)(gasAvailable - gasAvailable / 64), gasLimit);
        }

        if (gasLimit >= long.MaxValue) return EvmExceptionType.OutOfGas;

        long gasLimitUl = (long)gasLimit;
        if (!UpdateGas(gasLimitUl, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        if (!transferValue.IsZero)
        {
            if (TTracingRefunds.Tracing) _txTracer.ReportExtraGasPressure(GasCostOf.CallStipend);
            gasLimitUl += GasCostOf.CallStipend;
        }

        if (env.CallDepth >= MaxCallDepth ||
            !transferValue.IsZero && _state.GetBalance(env.ExecutingAccount) < transferValue)
        {
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();

            if (TTracingInstructions.Tracing)
            {
                // very specific for Parity trace, need to find generalization - very peculiar 32 length...
                ReadOnlyMemory<byte>? memoryTrace = vmState.Memory.Inspect(in dataOffset, 32);
                _txTracer.ReportMemoryChange(dataOffset, memoryTrace is null ? ReadOnlySpan<byte>.Empty : memoryTrace.Value.Span);
            }

            if (TLogger.Tracing) _logger.Trace("FAIL - call depth");
            if (TTracingInstructions.Tracing) _txTracer.ReportOperationRemainingGas(gasAvailable);
            if (TTracingInstructions.Tracing) _txTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);

            UpdateGasUp(gasLimitUl, ref gasAvailable);
            if (TTracingInstructions.Tracing) _txTracer.ReportGasUpdateForVmTrace(gasLimitUl, gasAvailable);
            return EvmExceptionType.None;
        }

        Snapshot snapshot = _worldState.TakeSnapshot();
        _state.SubtractFromBalance(caller, transferValue, spec);

        if (TLogger.Tracing) _logger.Trace($"Tx call gas {gasLimitUl}");
        if (As<UInt256, Vector256<byte>>(ref outputLength) == default)
        {
            // TODO: when output length is 0 outputOffset can have any value really
            // and the value does not matter and it can cause trouble when beyond long range
            outputOffset = default;
        }

        ExecutionType executionType = GetCallExecutionType(instruction, env.IsPostMerge());
        returnData = new EvmState(
            gasLimitUl,
            env: new
            (
                txExecutionContext: in env.TxExecutionContext,
                callDepth: env.CallDepth + 1,
                caller: caller,
                codeSource: codeSource,
                executingAccount: target,
                transferValue: transferValue,
                value: callValue,
                inputData: vmState.Memory.Load(in dataOffset, dataLength),
                codeInfo: GetCachedCodeInfo(_worldState, codeSource, spec)
            ),
            executionType,
            isTopLevel: false,
            snapshot,
            (long)outputOffset,
            (long)outputLength,
            instruction == Instruction.STATICCALL || vmState.IsStatic,
            vmState,
            isContinuation: false,
            isCreateOnPreExistingAccount: false,
            _txTracer,
            _worldState,
            spec);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private EvmExceptionType InstructionSelfDestruct(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
    {
        if (vmState.IsStatic) return EvmExceptionType.StaticCallViolation;

        IReleaseSpec spec = vmState.Spec;
        if (spec.UseShanghaiDDosProtection)
        {
            gasAvailable -= GasCostOf.SelfDestructEip150;
        }

        Metrics.SelfDestructs++;

        Address inheritor = stack.PopAddress();
        if (inheritor is null) return EvmExceptionType.StackUnderflow;
        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, inheritor, spec, false)) return EvmExceptionType.OutOfGas;

        Address executingAccount = vmState.Env.ExecutingAccount;
        bool createInSameTx = vmState.CreateList.Contains(executingAccount);
        if (!spec.SelfdestructOnlyOnSameTransaction || createInSameTx)
            vmState.DestroyList.Add(executingAccount);

        UInt256 result = _state.GetBalance(executingAccount);
        if (_txTracer.IsTracingActions) _txTracer.ReportSelfDestruct(executingAccount, result, inheritor);
        if (spec.ClearEmptyAccountWhenTouched && !result.IsZero && _state.IsDeadAccount(inheritor))
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return EvmExceptionType.OutOfGas;
        }

        bool inheritorAccountExists = _state.AccountExists(inheritor);
        if (!spec.ClearEmptyAccountWhenTouched && !inheritorAccountExists && spec.UseShanghaiDDosProtection)
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return EvmExceptionType.OutOfGas;
        }

        if (!inheritorAccountExists)
        {
            _state.CreateAccount(inheritor, result);
        }
        else if (!inheritor.Equals(executingAccount))
        {
            _state.AddToBalance(inheritor, result, spec);
        }

        if (spec.SelfdestructOnlyOnSameTransaction && !createInSameTx && inheritor.Equals(executingAccount))
            return EvmExceptionType.None; // don't burn eth when contract is not destroyed per EIP clarification

        _state.SubtractFromBalance(executingAccount, result, spec);
        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (EvmExceptionType exceptionType, EvmState? callState) InstructionCreate(EvmState vmState, ref EvmStack stack, ref long gasAvailable, Instruction instruction)
    {
        Metrics.Creates++;

        IReleaseSpec spec = vmState.Spec;
        if (!spec.Create2OpcodeEnabled && instruction == Instruction.CREATE2) return (EvmExceptionType.BadInstruction, null);

        if (vmState.IsStatic) return (EvmExceptionType.StaticCallViolation, null);

        ref readonly ExecutionEnvironment env = ref vmState.Env;

        // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
        if (!_state.AccountExists(env.ExecutingAccount))
        {
            _state.CreateAccount(env.ExecutingAccount, UInt256.Zero);
        }

        if (!stack.PopUInt256(out UInt256 value) ||
            !stack.PopUInt256(out UInt256 memoryPositionOfInitCode) ||
            !stack.PopUInt256(out UInt256 initCodeLength))
            return (EvmExceptionType.StackUnderflow, null);

        Span<byte> salt = default;
        if (instruction == Instruction.CREATE2)
        {
            salt = stack.PopWord256();
        }

        //EIP-3860
        if (spec.IsEip3860Enabled)
        {
            if (initCodeLength > spec.MaxInitCodeSize) return (EvmExceptionType.OutOfGas, null);
        }

        long gasCost = GasCostOf.Create +
                       (spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * EvmPooledMemory.Div32Ceiling(initCodeLength) : 0) +
                       (instruction == Instruction.CREATE2
                           ? GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(initCodeLength)
                           : 0);

        if (!UpdateGas(gasCost, ref gasAvailable)) return (EvmExceptionType.OutOfGas, null);

        if (!UpdateMemoryCost(vmState, ref gasAvailable, in memoryPositionOfInitCode, initCodeLength)) return (EvmExceptionType.OutOfGas, null);

        // TODO: copy pasted from CALL / DELEGATECALL, need to move it outside?
        if (env.CallDepth >= MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
        {
            // TODO: need a test for this
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (EvmExceptionType.None, null);
        }

        ReadOnlyMemory<byte> initCode = vmState.Memory.Load(in memoryPositionOfInitCode, initCodeLength);

        UInt256 balance = _state.GetBalance(env.ExecutingAccount);
        if (value > balance)
        {
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (EvmExceptionType.None, null);
        }

        UInt256 accountNonce = _state.GetNonce(env.ExecutingAccount);
        UInt256 maxNonce = ulong.MaxValue;
        if (accountNonce >= maxNonce)
        {
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (EvmExceptionType.None, null);
        }

        if (_txTracer.IsTracingInstructions) EndInstructionTrace(gasAvailable, vmState.Memory.Size);
        // todo: === below is a new call - refactor / move

        long callGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
        if (!UpdateGas(callGas, ref gasAvailable)) return (EvmExceptionType.OutOfGas, null);

        Address contractAddress = instruction == Instruction.CREATE
            ? ContractAddress.From(env.ExecutingAccount, _state.GetNonce(env.ExecutingAccount))
            : ContractAddress.From(env.ExecutingAccount, salt, initCode.Span);

        if (spec.UseHotAndColdStorage)
        {
            // EIP-2929 assumes that warm-up cost is included in the costs of CREATE and CREATE2
            vmState.WarmUp(contractAddress);
        }

        _state.IncrementNonce(env.ExecutingAccount);

        Snapshot snapshot = _worldState.TakeSnapshot();

        bool accountExists = _state.AccountExists(contractAddress);
        if (accountExists && (GetCachedCodeInfo(_worldState, contractAddress, spec).MachineCode.Length != 0 ||
                              _state.GetNonce(contractAddress) != 0))
        {
            /* we get the snapshot before this as there is a possibility with that we will touch an empty account and remove it even if the REVERT operation follows */
            if (TLogger.Tracing) _logger.Trace($"Contract collision at {contractAddress}");
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (EvmExceptionType.None, null);
        }

        if (accountExists)
        {
            _state.UpdateStorageRoot(contractAddress, Keccak.EmptyTreeHash);
        }
        else if (_state.IsDeadAccount(contractAddress))
        {
            _state.ClearStorage(contractAddress);
        }

        _state.SubtractFromBalance(env.ExecutingAccount, value, spec);

        // Do not add the initCode to the cache as it is
        // pointing to data in this tx and will become invalid
        // for another tx as returned to pool.
        CodeInfo codeInfo = new(initCode);
        codeInfo.AnalyseInBackgroundIfRequired();

        EvmState callState = new(
            callGas,
            env: new
            (
                txExecutionContext: in env.TxExecutionContext,
                callDepth: env.CallDepth + 1,
                caller: env.ExecutingAccount,
                executingAccount: contractAddress,
                codeSource: null,
                codeInfo: codeInfo,
                inputData: default,
                transferValue: value,
                value: value
            ),
            instruction == Instruction.CREATE2 ? ExecutionType.CREATE2 : ExecutionType.CREATE,
            false,
            snapshot,
            0L,
            0L,
            vmState.IsStatic,
            vmState,
            false,
            accountExists,
            _txTracer,
            _worldState,
            spec);

        return (EvmExceptionType.None, callState);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private EvmExceptionType InstructionLog(EvmState vmState, ref EvmStack stack, ref long gasAvailable, Instruction instruction)
    {
        if (vmState.IsStatic) return EvmExceptionType.StaticCallViolation;

        if (!stack.PopUInt256(out UInt256 position)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 length)) return EvmExceptionType.StackUnderflow;
        long topicsCount = instruction - Instruction.LOG0;
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in position, length)) return EvmExceptionType.OutOfGas;
        if (!UpdateGas(
                GasCostOf.Log + topicsCount * GasCostOf.LogTopic +
                (long)length * GasCostOf.LogData, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        ReadOnlyMemory<byte> data = vmState.Memory.Load(in position, length);
        Hash256[] topics = new Hash256[topicsCount];
        for (int i = 0; i < topicsCount; i++)
        {
            topics[i] = new Hash256(stack.PopWord256());
        }

        LogEntry logEntry = new(
            vmState.Env.ExecutingAccount,
            data.ToArray(),
            topics);
        vmState.Logs.Add(logEntry);

        if (_txTracer.IsTracingLogs)
        {
            _txTracer.ReportLog(logEntry);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private EvmExceptionType InstructionSLoad<TTracingInstructions, TTracingStorage>(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
        where TTracingInstructions : struct, IIsTracing
        where TTracingStorage : struct, IIsTracing
    {
        IReleaseSpec spec = vmState.Spec;
        Metrics.SloadOpcode++;
        gasAvailable -= spec.GetSLoadCost();

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);
        if (!ChargeStorageAccessGas(
            ref gasAvailable,
            vmState,
            in storageCell,
            StorageAccessType.SLOAD,
            spec)) return EvmExceptionType.OutOfGas;

        ReadOnlySpan<byte> value = _state.Get(in storageCell);
        stack.PushBytes(value);
        if (TTracingStorage.Tracing)
        {
            _txTracer.LoadOperationStorage(storageCell.Address, result, value);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private EvmExceptionType InstructionSStore<TTracingInstructions, TTracingRefunds, TTracingStorage>(EvmState vmState, ref EvmStack stack, ref long gasAvailable)
        where TTracingInstructions : struct, IIsTracing
        where TTracingRefunds : struct, IIsTracing
        where TTracingStorage : struct, IIsTracing
    {
        Metrics.SstoreOpcode++;
        IReleaseSpec spec = vmState.Spec;
        if (vmState.IsStatic) return EvmExceptionType.StaticCallViolation;
        // fail fast before the first storage read if gas is not enough even for reset
        if (!spec.UseNetGasMetering && !UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;

        if (spec.UseNetGasMeteringWithAStipendFix)
        {
            if (TTracingRefunds.Tracing)
                _txTracer.ReportExtraGasPressure(GasCostOf.CallStipend - spec.GetNetMeteredSStoreCost() + 1);
            if (gasAvailable <= GasCostOf.CallStipend) return EvmExceptionType.OutOfGas;
        }

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        ReadOnlySpan<byte> bytes = stack.PopWord256();
        bool newIsZero = bytes.IsZero();
        bytes = !newIsZero ? bytes.WithoutLeadingZeros() : BytesZero;

        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);

        if (!ChargeStorageAccessGas(
                ref gasAvailable,
                vmState,
                in storageCell,
                StorageAccessType.SSTORE,
                spec)) return EvmExceptionType.OutOfGas;

        ReadOnlySpan<byte> currentValue = _state.Get(in storageCell);
        // Console.WriteLine($"current: {currentValue.ToHexString()} newValue {newValue.ToHexString()}");
        bool currentIsZero = currentValue.IsZero();

        bool newSameAsCurrent = (newIsZero && currentIsZero) || Bytes.AreEqual(currentValue, bytes);
        long sClearRefunds = RefundOf.SClear(spec.IsEip3529Enabled);

        if (!spec.UseNetGasMetering) // note that for this case we already deducted 5000
        {
            if (newIsZero)
            {
                if (!newSameAsCurrent)
                {
                    vmState.Refund += sClearRefunds;
                    if (TTracingRefunds.Tracing) _txTracer.ReportRefund(sClearRefunds);
                }
            }
            else if (currentIsZero)
            {
                if (!UpdateGas(GasCostOf.SSet - GasCostOf.SReset, ref gasAvailable)) return EvmExceptionType.OutOfGas;
            }
        }
        else // net metered
        {
            if (newSameAsCurrent)
            {
                if (!UpdateGas(spec.GetNetMeteredSStoreCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;
            }
            else // net metered, C != N
            {
                Span<byte> originalValue = _state.GetOriginal(in storageCell);
                bool originalIsZero = originalValue.IsZero();

                bool currentSameAsOriginal = Bytes.AreEqual(originalValue, currentValue);
                if (currentSameAsOriginal)
                {
                    if (currentIsZero)
                    {
                        if (!UpdateGas(GasCostOf.SSet, ref gasAvailable)) return EvmExceptionType.OutOfGas;
                    }
                    else // net metered, current == original != new, !currentIsZero
                    {
                        if (!UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;

                        if (newIsZero)
                        {
                            vmState.Refund += sClearRefunds;
                            if (TTracingRefunds.Tracing) _txTracer.ReportRefund(sClearRefunds);
                        }
                    }
                }
                else // net metered, new != current != original
                {
                    long netMeteredStoreCost = spec.GetNetMeteredSStoreCost();
                    if (!UpdateGas(netMeteredStoreCost, ref gasAvailable)) return EvmExceptionType.OutOfGas;

                    if (!originalIsZero) // net metered, new != current != original != 0
                    {
                        if (currentIsZero)
                        {
                            vmState.Refund -= sClearRefunds;
                            if (TTracingRefunds.Tracing) _txTracer.ReportRefund(-sClearRefunds);
                        }

                        if (newIsZero)
                        {
                            vmState.Refund += sClearRefunds;
                            if (TTracingRefunds.Tracing) _txTracer.ReportRefund(sClearRefunds);
                        }
                    }

                    bool newSameAsOriginal = Bytes.AreEqual(originalValue, bytes);
                    if (newSameAsOriginal)
                    {
                        long refundFromReversal;
                        if (originalIsZero)
                        {
                            refundFromReversal = spec.GetSetReversalRefund();
                        }
                        else
                        {
                            refundFromReversal = spec.GetClearReversalRefund();
                        }

                        vmState.Refund += refundFromReversal;
                        if (TTracingRefunds.Tracing) _txTracer.ReportRefund(refundFromReversal);
                    }
                }
            }
        }

        if (!newSameAsCurrent)
        {
            _state.Set(in storageCell, newIsZero ? BytesZero : bytes.ToArray());
        }

        if (TTracingInstructions.Tracing)
        {
            ReadOnlySpan<byte> valueToStore = newIsZero ? BytesZero.AsSpan() : bytes;
            byte[] storageBytes = new byte[32]; // do not stackalloc here
            storageCell.Index.ToBigEndian(storageBytes);
            _txTracer.ReportStorageChange(storageBytes, valueToStore);
        }

        if (TTracingStorage.Tracing)
        {
            _txTracer.SetOperationStorage(storageCell.Address, result, bytes, currentValue);
        }

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref readonly CallResult GetFailureReturn<TTracingInstructions>(long gasAvailable, EvmExceptionType exceptionType)
        where TTracingInstructions : struct, IIsTracing
    {
        if (TTracingInstructions.Tracing) EndInstructionTraceError(gasAvailable, exceptionType);

        switch (exceptionType)
        {
            case EvmExceptionType.OutOfGas: return ref CallResult.OutOfGasException;
            case EvmExceptionType.BadInstruction: return ref CallResult.InvalidInstructionException;
            case EvmExceptionType.StaticCallViolation: return ref CallResult.StaticCallViolationException;
            case EvmExceptionType.StackOverflow: return ref CallResult.StackOverflowException;
            case EvmExceptionType.StackUnderflow: return ref CallResult.StackUnderflowException;
            case EvmExceptionType.InvalidJumpDestination: return ref CallResult.InvalidJumpDestination;
            case EvmExceptionType.AccessViolation: return ref CallResult.AccessViolationException;
            default: throw new ArgumentOutOfRangeException(nameof(exceptionType), exceptionType, "");
        };
    }

    private static void UpdateCurrentState(EvmState state, int pc, long gas, int stackHead)
    {
        state.ProgramCounter = pc;
        state.GasAvailable = gas;
        state.DataStackHead = stackHead;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StartInstructionTrace(Instruction instruction, EvmState vmState, long gasAvailable, int programCounter, in EvmStack stackValue)
    {
        _txTracer.StartOperation(programCounter, instruction, gasAvailable, vmState.Env);
        if (_txTracer.IsTracingMemory)
        {
            _txTracer.SetOperationMemory(vmState.Memory.GetTrace());
            _txTracer.SetOperationMemorySize(vmState.Memory.Size);
        }

        if (_txTracer.IsTracingStack)
        {
            Memory<byte> stackMemory = vmState.DataStack.AsMemory().Slice(0, stackValue.Head * EvmStack.WordSize);
            _txTracer.SetOperationStack(new TraceStack(stackMemory));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EndInstructionTrace(long gasAvailable, ulong memorySize)
    {
        _txTracer.ReportOperationRemainingGas(gasAvailable);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EndInstructionTraceError(long gasAvailable, EvmExceptionType evmExceptionType)
    {
        _txTracer.ReportOperationRemainingGas(gasAvailable);
        _txTracer.ReportOperationError(evmExceptionType);
    }

    private static ExecutionType GetCallExecutionType(Instruction instruction, bool isPostMerge = false)
    {
        ExecutionType executionType;
        if (instruction == Instruction.CALL)
        {
            executionType = ExecutionType.CALL;
        }
        else if (instruction == Instruction.DELEGATECALL)
        {
            executionType = ExecutionType.DELEGATECALL;
        }
        else if (instruction == Instruction.STATICCALL)
        {
            executionType = ExecutionType.STATICCALL;
        }
        else if (instruction == Instruction.CALLCODE)
        {
            executionType = ExecutionType.CALLCODE;
        }
        else
        {
            throw new NotSupportedException($"Execution type is undefined for {instruction.GetName(isPostMerge)}");
        }

        return executionType;
    }
}
