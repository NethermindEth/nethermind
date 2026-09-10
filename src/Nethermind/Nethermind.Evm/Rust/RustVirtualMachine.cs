// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if RUST_EVM
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Evm.Rust;

/// <summary>
/// The Rust interpreter (nethermind-rust's <c>nm-evm</c>) behind <see cref="IVirtualMachine"/>.
/// </summary>
/// <remarks>
/// <para>
/// The transaction stays this side's: the processor charges the intrinsic gas, moves the value,
/// pays the fees and keeps the state. What crosses is the top frame — code, caller, value, gas,
/// what the transaction has warmed — and what comes back is what the frame left: output, logs,
/// refund, the accounts to destroy, and every account and slot it changed, which are written
/// into <see cref="IWorldState"/> here. While the frame runs, the interpreter reads state through
/// four callbacks, once per account, slot or code it has not seen; everything else — the
/// arithmetic, the stack, the memory, the calls between contracts — stays on the other side.
/// </para>
/// <para>
/// Anything the interpreter does not cover — a tracer that wants to see instructions, a fork it
/// does not know, a precompile it has not ported — goes to the C# interpreter instead, so the
/// answer is always there.
/// </para>
/// </remarks>
public sealed unsafe class RustVirtualMachine(IBlockhashProvider? blockHashProvider, ISpecProvider? specProvider, ILogManager? logManager) : IVirtualMachine
{
    private readonly EthereumVirtualMachine _inner = new(blockHashProvider, specProvider, logManager);
    private readonly IBlockhashProvider _blockHashProvider = blockHashProvider ?? throw new ArgumentNullException(nameof(blockHashProvider));
    private readonly ulong _chainId = specProvider?.ChainId ?? throw new ArgumentNullException(nameof(specProvider));
    private readonly ILogger _logger = logManager?.GetClassLogger<RustVirtualMachine>() ?? NullLogger.Instance;

    // What the callbacks answer from while a frame runs.
    private IWorldState? _worldState;
    private IReleaseSpec? _spec;
    private BlockHeader? _header;
    private readonly List<GCHandle> _pinned = [];
    private GCHandle _self;
    // A record the world state refused — on a stateless run, a witness without the code it was
    // asked about. The frame then goes to the C# interpreter, which throws the exception the
    // block is rejected for.
    private bool _observerFailed;
    // What the world state threw at a callback: on a stateless run, the witness missing what was
    // asked for. It is thrown again once the interpreter has returned, as the C# interpreter's
    // read would have thrown it, so the block is rejected for the same reason.
    private Exception? _readException;

    /// <summary>Whether the interpreter is linked in and speaks the ABI this binding expects.</summary>
    public static bool IsAvailable { get; } = Probe();


    /// <summary>How many frames went to the Rust interpreter, and how many the C# one took.</summary>
    public static long RustFrames, CSharpFrames;

    /// <summary>Ticks spent inside the interpreter's call, and in the write-back after it.</summary>
    public static long InsideTicks, WriteBackTicks;

    /// <summary>How often each callback was called, and the ticks the observer ones took.</summary>
    public static long AccountCalls, StorageCalls, CodeCalls, ObserverCalls, ObserverTicks;

    private static bool Probe()
    {
        if (Environment.GetEnvironmentVariable("NETHERMIND_RUST_EVM") is "0" or "false")
            return false;
        try
        {
            if (RustEvmNative.AbiVersion() != RustEvmNative.ExpectedAbiVersion)
                return false;
            RustEvmNative.Init();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public ref readonly BlockExecutionContext BlockExecutionContext => ref _inner.BlockExecutionContext;
    public ref readonly TxExecutionContext TxExecutionContext => ref _inner.TxExecutionContext;
    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => _inner.SetBlockExecutionContext(in blockExecutionContext);
    public void SetTxExecutionContext(in TxExecutionContext txExecutionContext) => _inner.SetTxExecutionContext(in txExecutionContext);
    public int OpCodeCount => _inner.OpCodeCount;
    public void FlushMetricsCounters() => _inner.FlushMetricsCounters();

    public TransactionSubstate ExecuteTransaction<TTracingInst>(VmState<EthereumGasPolicy> state, IWorldState worldState, ITxTracer txTracer)
        where TTracingInst : struct, IFlag
    {
        IReleaseSpec spec = BlockExecutionContext.Spec;
        if (TTracingInst.IsActive || WantsToWatchTheFrame(txTracer) || ForkNumber(spec) is null)
        {
            CSharpFrames++;
            return _inner.ExecuteTransaction<TTracingInst>(state, worldState, txTracer);
        }

        RustFrames++;
        return Run(state, worldState, spec, txTracer);
    }

    /// <summary>
    /// Whether the tracer wants to hear from inside the frame — actions, instructions, refunds, logs
    /// and the like, which the interpreter reports as it runs. A tracer that only wants the receipt,
    /// the output or the fees hears those from the processor and is served either way.
    /// </summary>
    private static bool WantsToWatchTheFrame(ITxTracer tracer) =>
        tracer.IsTracingActions || tracer.IsTracingInstructions || tracer.IsTracingRefunds
        || tracer.IsTracingOpLevelStorage || tracer.IsTracingStack || tracer.IsTracingMemory
        || tracer.IsTracingLogs || tracer.IsTracingCode || tracer.IsTracingBlockHash || tracer.IsTracingAccess;

    /// <summary>The number <c>nm-ffi</c> gives the fork, or null for one it does not know.</summary>
    private static byte? ForkNumber(IReleaseSpec spec) => spec.Name?.ToLowerInvariant() switch
    {
        "cancun" => 0,
        "prague" => 1,
        "osaka" => 2,
        "bpo1" => 3,
        "bpo2" => 4,
        "bpo3" => 5,
        "bpo4" => 6,
        "bpo5" => 7,
        "amsterdam" => 8,
        _ => null,
    };

    private TransactionSubstate Run(VmState<EthereumGasPolicy> state, IWorldState worldState, IReleaseSpec spec, ITxTracer txTracer)
    {
        ref readonly BlockExecutionContext block = ref BlockExecutionContext;
        ref readonly TxExecutionContext tx = ref TxExecutionContext;
        BlockHeader header = block.Header;
        ExecutionEnvironment env = state.Env;

        // The warm sets, the blob hashes and the frame's bytes are pinned for the call.
        List<Address> warmAddresses = [.. state.AccessTracker.AccessedAddresses];
        List<StorageCell> warmCells = [.. state.AccessTracker.AccessedStorageCells];
        RustEvmNative.FfiAddress[] addresses = new RustEvmNative.FfiAddress[warmAddresses.Count];
        for (int i = 0; i < warmAddresses.Count; i++) addresses[i] = ToFfi(warmAddresses[i]);
        RustEvmNative.FfiStorageCell[] cells = new RustEvmNative.FfiStorageCell[warmCells.Count];
        for (int i = 0; i < warmCells.Count; i++)
        {
            UInt256 index = warmCells[i].Index;
            cells[i].Address = ToFfi(warmCells[i].Address);
            cells[i].Key = ToFfi(in index);
        }
        byte[][] blobHashes = tx.BlobVersionedHashes ?? [];
        RustEvmNative.FfiHash[] hashes = new RustEvmNative.FfiHash[blobHashes.Length];
        for (int i = 0; i < blobHashes.Length; i++) hashes[i] = ToFfi(blobHashes[i]);

        ReadOnlyMemory<byte> code = env.CodeInfo.IsPrecompile ? ReadOnlyMemory<byte>.Empty : env.CodeInfo.Code;
        ReadOnlyMemory<byte> input = env.InputData;

        _worldState = worldState;
        _spec = spec;
        _header = header;
        _observerFailed = false;
        _readException = null;
        _self = GCHandle.Alloc(this);
        RustEvmNative.FfiResult result = default;
        try
        {
            fixed (RustEvmNative.FfiAddress* addressesPtr = addresses)
            fixed (RustEvmNative.FfiStorageCell* cellsPtr = cells)
            fixed (RustEvmNative.FfiHash* hashesPtr = hashes)
            fixed (byte* codePtr = code.Span)
            fixed (byte* inputPtr = input.Span)
            {
                RustEvmNative.FfiRequest request = new()
                {
                    Fork = ForkNumber(spec)!.Value,
                    Gnosis = (byte)(_chainId == 100 ? 1 : 0),
                    ChainId = ToFfi((UInt256)_chainId),
                    Block = new RustEvmNative.FfiBlock
                    {
                        Number = (ulong)header.Number,
                        Timestamp = header.Timestamp,
                        GasLimit = (ulong)header.GasLimit,
                        Beneficiary = ToFfi(block.Coinbase),
                        BaseFeePerGas = ToFfi(in header.BaseFeePerGas),
                        PrevRandao = ToFfi(in block.PrevRandao),
                        ExcessBlobGas = header.ExcessBlobGas ?? 0,
                        HasExcessBlobGas = (byte)(header.ExcessBlobGas is null ? 0 : 1),
                        SlotNumber = header.SlotNumber ?? 0,
                        HasSlotNumber = (byte)(header.SlotNumber is null ? 0 : 1),
                        BlobBaseFee = ToFfi(new UInt256(block.BlobBaseFee.Bytes, isBigEndian: true)),
                    },
                    Tx = new RustEvmNative.FfiTx
                    {
                        Origin = ToFfi(new Address(in tx.Origin)),
                        GasPrice = ToFfi(in tx.GasPrice),
                        BlobVersionedHashes = hashesPtr,
                        BlobVersionedHashCount = (nuint)hashes.Length,
                    },
                    Frame = new RustEvmNative.FfiFrame
                    {
                        Gas = ToFfi(in state.Gas),
                        ExecutionType = (byte)state.ExecutionType,
                        IsStatic = (byte)(state.IsStatic ? 1 : 0),
                        IsCreateStateGasCharged = (byte)(state.IsCreateStateGasCharged ? 1 : 0),
                        ExecutingAccount = ToFfi(env.ExecutingAccount),
                        Caller = ToFfi(env.Caller),
                        CodeSource = env.CodeSource is null ? default : ToFfi(env.CodeSource),
                        HasCodeSource = (byte)(env.CodeSource is null ? 0 : 1),
                        Code = new RustEvmNative.FfiBytes { Ptr = codePtr, Len = (nuint)code.Length },
                        CodeIsPrecompile = (byte)(env.CodeInfo.IsPrecompile ? 1 : 0),
                        Value = ToFfi(in env.Value),
                        Input = new RustEvmNative.FfiBytes { Ptr = inputPtr, Len = (nuint)input.Length },
                    },
                    Warm = new RustEvmNative.FfiWarm
                    {
                        Addresses = addressesPtr,
                        AddressCount = (nuint)addresses.Length,
                        Cells = cellsPtr,
                        CellCount = (nuint)cells.Length,
                    },
                };
                RustEvmNative.FfiCallbacks callbacks = new()
                {
                    Context = (void*)GCHandle.ToIntPtr(_self),
                    Account = &AccountCallback,
                    Storage = &StorageCallback,
                    Code = &CodeCallback,
                    BlockHash = &BlockHashCallback,
                    AccountRead = &AccountReadCallback,
                    BytecodeAccess = &BytecodeAccessCallback,
                    AccountAccess = &AccountReadCallback,
                };

                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                int status = RustEvmNative.Execute(&request, &callbacks, &result);
                InsideTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                if (_readException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_readException);
                }

                if (status != RustEvmNative.StatusOk || _observerFailed)
                {
                    // The interpreter could not run this frame; the C# one can. Nothing was
                    // written on this side, so the state is as the processor left it.
                    if (_logger.IsDebug) _logger.Debug($"Rust EVM declined the frame with status {status}; running it here");
                    RustEvmNative.Free(&result);
                    RustFrames--;
                    CSharpFrames++;
                    return _inner.ExecuteTransaction<OffFlag>(state, worldState, txTracer);
                }

                return Take(state, worldState, spec, &result, txTracer.IsTracing);
            }
        }
        finally
        {
            RustEvmNative.Free(&result);
            foreach (GCHandle handle in _pinned) handle.Free();
            _pinned.Clear();
            _self.Free();
            _worldState = null;
            _spec = null;
            _header = null;
        }
    }

    /// <summary>Builds the substate and writes the frame's changes into the world state.</summary>
    private static TransactionSubstate Take(VmState<EthereumGasPolicy> state, IWorldState worldState, IReleaseSpec spec, RustEvmNative.FfiResult* resultPtr, bool isTracerConnected)
    {
        ref RustEvmNative.FfiResult result = ref *resultPtr;
        state.Gas = FromFfi(in result.Gas);

        EvmExceptionType exception = (EvmExceptionType)result.Exception;
        bool shouldRevert = result.ShouldRevert != 0;
        if (exception != EvmExceptionType.None)
        {
            return new TransactionSubstate(exception, isTracerConnected) { ShouldRestoreRipemdTouch = result.RestoreRipemdTouch != 0 };
        }

        byte[] output = result.Output.Len == 0 ? [] : new ReadOnlySpan<byte>(result.Output.Ptr, (int)result.Output.Len).ToArray();

        JournalCollection<LogEntry> logs = [];
        for (nuint i = 0; i < result.LogCount; i++)
        {
            ref RustEvmNative.FfiLog log = ref result.Logs[i];
            Hash256[] topics = new Hash256[log.TopicCount];
            for (nuint t = 0; t < log.TopicCount; t++)
            {
                RustEvmNative.FfiHash* topic = log.Topics + t;
                topics[t] = new Hash256(new ReadOnlySpan<byte>(topic->Bytes, 32));
            }
            byte[] data = log.Data.Len == 0 ? [] : new ReadOnlySpan<byte>(log.Data.Ptr, (int)log.Data.Len).ToArray();
            logs.Add(new LogEntry(FromFfi(in log.Address), data, topics));
        }

        JournalSet<Address>? destroyList = null;
        if (result.DestroyCount > 0)
        {
            destroyList = new JournalSet<Address>(Address.EqualityComparer);
            for (nuint i = 0; i < result.DestroyCount; i++) destroyList.Add(FromFfi(in result.Destroy[i]));
        }

        if (!shouldRevert)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            WriteBack(worldState, spec, resultPtr);
            WriteBackTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        }

        return new TransactionSubstate(output, result.Refund, destroyList, logs, shouldRevert, isTracerConnected)
        {
            ShouldRestoreRipemdTouch = result.RestoreRipemdTouch != 0,
        };
    }

    /// <summary>Writes what the frame changed into the world state, as the C# interpreter would have as it went.</summary>
    private static void WriteBack(IWorldState worldState, IReleaseSpec spec, RustEvmNative.FfiResult* resultPtr)
    {
        ref RustEvmNative.FfiResult result = ref *resultPtr;
        for (nuint i = 0; i < result.WipedCount; i++)
        {
            worldState.ClearStorage(FromFfi(in result.Wiped[i]));
        }

        for (nuint i = 0; i < result.AccountCount; i++)
        {
            ref RustEvmNative.FfiAccountChange change = ref result.Accounts[i];
            Address address = FromFfi(in change.Address);
            if (change.Exists == 0)
            {
                if (worldState.AccountExists(address))
                {
                    worldState.DeleteAccount(address);
                }
                continue;
            }

            UInt256 balance = FromFfi(in change.Balance);
            if (!worldState.AccountExists(address))
            {
                worldState.CreateAccount(address, in balance, change.Nonce);
            }
            else
            {
                UInt256 current = worldState.GetBalance(address);
                if (balance > current)
                {
                    UInt256 delta = balance - current;
                    worldState.AddToBalance(address, in delta, spec, out _);
                }
                else if (balance < current)
                {
                    UInt256 delta = current - balance;
                    worldState.SubtractFromBalance(address, in delta, spec, out _);
                }
                else
                {
                    // Touched but no richer: the touch is what EIP-161 needs to know.
                    worldState.AddToBalance(address, in UInt256.Zero, spec, out _);
                }

                if (worldState.GetNonce(address) != change.Nonce)
                {
                    worldState.SetNonce(address, change.Nonce);
                }
            }

            if (change.HasCode != 0)
            {
                byte[] code = change.Code.Len == 0 ? [] : new ReadOnlySpan<byte>(change.Code.Ptr, (int)change.Code.Len).ToArray();
                ValueHash256 codeHash = new(new ReadOnlySpan<byte>((&result.Accounts[i])->CodeHash.Bytes, 32));
                worldState.InsertCode(address, in codeHash, code, spec);
            }
        }

        for (nuint i = 0; i < result.StorageCount; i++)
        {
            ref RustEvmNative.FfiStorageChange change = ref result.Storage[i];
            StorageCell cell = new(FromFfi(in change.Address), FromFfi(in change.Key));
            UInt256 value = FromFfi(in change.Value);
            worldState.Set(in cell, value.IsZero ? VirtualMachineStatics.BytesZero : value.ToBigEndian().WithoutLeadingZeros().ToArray());
        }
    }

    /* The callbacks */

    private static RustVirtualMachine Of(void* context) => (RustVirtualMachine)GCHandle.FromIntPtr((IntPtr)context).Target!;

    [UnmanagedCallersOnly]
    private static int AccountCallback(void* context, RustEvmNative.FfiAddress* address, RustEvmNative.FfiAccount* account)
    {
        try
        {
            AccountCalls++;
            RustVirtualMachine machine = Of(context);
            if (!machine._worldState!.TryGetAccount(FromFfi(in *address), out AccountStruct found))
            {
                return 0;
            }
            UInt256 balance = found.Balance;
            ValueHash256 codeHash = found.CodeHash;
            ValueHash256 storageRoot = found.StorageRoot;
            account->Nonce = found.Nonce;
            account->Balance = ToFfi(in balance);
            account->CodeHash = ToFfi(in codeHash);
            account->StorageRoot = ToFfi(in storageRoot);
            return 1;
        }
        catch (Exception exception)
        {
            Of(context)._readException ??= exception;
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    private static int StorageCallback(void* context, RustEvmNative.FfiAddress* address, RustEvmNative.FfiU256* key, RustEvmNative.FfiU256* value)
    {
        try
        {
            StorageCalls++;
            RustVirtualMachine machine = Of(context);
            StorageCell cell = new(FromFfi(in *address), FromFfi(in *key));
            ReadOnlySpan<byte> bytes = machine._worldState!.Get(in cell);
            UInt256 read = bytes.IsEmpty ? UInt256.Zero : new UInt256(bytes, isBigEndian: true);
            *value = ToFfi(in read);
            return 1;
        }
        catch (Exception exception)
        {
            Of(context)._readException ??= exception;
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    private static int CodeCallback(void* context, RustEvmNative.FfiHash* codeHash, RustEvmNative.FfiBytes* code)
    {
        try
        {
            CodeCalls++;
            RustVirtualMachine machine = Of(context);
            ValueHash256 hash = new(new ReadOnlySpan<byte>(codeHash->Bytes, 32));
            byte[]? found = machine._worldState!.GetCode(in hash);
            if (found is null)
            {
                // Code the state cannot produce: on a stateless run, a witness that left it out.
                // The C# interpreter is the one to say so, with the exception the block is
                // rejected for.
                return -1;
            }
            if (found.Length == 0)
            {
                *code = default;
                return 1;
            }
            // The interpreter copies the bytes before this call's caller returns; the array is
            // held still until the frame is done, which is later than that.
            GCHandle handle = GCHandle.Alloc(found, GCHandleType.Pinned);
            machine._pinned.Add(handle);
            *code = new RustEvmNative.FfiBytes { Ptr = (byte*)handle.AddrOfPinnedObject(), Len = (nuint)found.Length };
            return 1;
        }
        catch (Exception exception)
        {
            Of(context)._readException ??= exception;
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    private static int BlockHashCallback(void* context, ulong number, RustEvmNative.FfiHash* hash)
    {
        try
        {
            RustVirtualMachine machine = Of(context);
            Hash256? found = machine._blockHashProvider.GetBlockhash(machine._header!, number, machine._spec!);
            if (found is null)
                return 0;
            *hash = ToFfi(found.Bytes);
            return 1;
        }
        catch (Exception exception)
        {
            Of(context)._readException ??= exception;
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    private static void AccountReadCallback(void* context, RustEvmNative.FfiAddress* address)
    {
        try
        {
            ObserverCalls++;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Of(context)._worldState!.AddAccountRead(FromFfi(in *address));
            ObserverTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        }
        catch (Exception exception)
        {
            RustVirtualMachine machine = Of(context);
            machine._readException ??= exception;
            machine._observerFailed = true;
        }
    }

    [UnmanagedCallersOnly]
    private static void BytecodeAccessCallback(void* context, RustEvmNative.FfiAddress* address)
    {
        try
        {
            ObserverCalls++;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            IWorldState worldState = Of(context)._worldState!;
            Address read = FromFfi(in *address);
            worldState.AddAccountRead(read);
            worldState.RecordBytecodeAccess(read);
            ObserverTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        }
        catch (Exception exception)
        {
            RustVirtualMachine machine = Of(context);
            machine._readException ??= exception;
            machine._observerFailed = true;
        }
    }

    /* Conversions */

    private static RustEvmNative.FfiU256 ToFfi(in UInt256 value) => Unsafe.As<UInt256, RustEvmNative.FfiU256>(ref Unsafe.AsRef(in value));
    private static UInt256 FromFfi(in RustEvmNative.FfiU256 value) => Unsafe.As<RustEvmNative.FfiU256, UInt256>(ref Unsafe.AsRef(in value));

    private static RustEvmNative.FfiAddress ToFfi(Address address)
    {
        RustEvmNative.FfiAddress result = default;
        address.Bytes.CopyTo(new Span<byte>(result.Bytes, 20));
        return result;
    }

    private static Address FromFfi(in RustEvmNative.FfiAddress address)
    {
        fixed (byte* bytes = address.Bytes)
        {
            return new Address(new ReadOnlySpan<byte>(bytes, 20));
        }
    }

    private static RustEvmNative.FfiHash ToFfi(ReadOnlySpan<byte> bytes)
    {
        RustEvmNative.FfiHash result = default;
        bytes.CopyTo(new Span<byte>(result.Bytes, 32));
        return result;
    }

    private static RustEvmNative.FfiHash ToFfi(in ValueHash256 hash) => ToFfi(hash.Bytes);

    private static RustEvmNative.FfiGas ToFfi(in EthereumGasPolicy gas) => new()
    {
        Remaining = gas.Value,
        StateReservoir = gas.StateReservoir,
        StateGasUsed = gas.StateGasUsed,
        StateGasSpill = gas.StateGasSpill,
        StateGasSpillRefunded = gas.StateGasSpillRefunded,
        OutOfGas = (byte)(gas.OutOfGas ? 1 : 0),
    };

    private static EthereumGasPolicy FromFfi(in RustEvmNative.FfiGas gas) => new()
    {
        Value = gas.Remaining,
        StateReservoir = gas.StateReservoir,
        StateGasUsed = gas.StateGasUsed,
        StateGasSpill = gas.StateGasSpill,
        StateGasSpillRefunded = gas.StateGasSpillRefunded,
        OutOfGas = gas.OutOfGas != 0,
    };
}
#endif
