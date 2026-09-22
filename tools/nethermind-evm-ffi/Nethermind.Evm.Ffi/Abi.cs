// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;

namespace Nethermind.Evm.Ffi;

/// <summary>Captures what the host needs back from one transaction.</summary>
internal sealed class ResultTracer : TxTracer
{
    public override bool IsTracingReceipt => true;

    public byte[] Output = [];
    public LogEntry[] Logs = [];
    public ulong GasSpent;
    public ulong GasRefunded;
    public bool Success;
    public string? Error;

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output,
        LogEntry[] logs, Hash256? stateRoot = null)
    {
        GasSpent = gasSpent.SpentGas;
        GasRefunded = gasSpent.GasRefund;
        Output = output ?? [];
        Logs = logs ?? [];
        Success = true;
        Error = null;
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output,
        string? error, Hash256? stateRoot = null)
    {
        GasSpent = gasSpent.SpentGas;
        GasRefunded = gasSpent.GasRefund;
        Output = output ?? [];
        Logs = [];
        Success = false;
        Error = error;
    }

    public void Reset()
    {
        Output = [];
        Logs = [];
        GasSpent = 0;
        GasRefunded = 0;
        Success = false;
        Error = null;
    }
}

/// <summary>One EVM and the state scope it runs in. Not thread-safe; the host owns one per thread.</summary>
internal sealed class Engine
{
    public required ITransactionProcessor Processor { get; init; }
    public required IWorldState State { get; init; }
    public required IDisposable Scope { get; init; }
    public required ISpecProvider Specs { get; init; }
    public required Diff Diff { get; init; }
    public required ResultTracer Tracer { get; init; }
    public ulong ChainId { get; init; }
    public BlockHeader? Header;
    public string? LastError;
}

/// <summary>The exported C ABI. See include/nethermind_evm.h for the contract.</summary>
public static unsafe class Abi
{
    private const uint AbiVersion = 1;

    /// <remarks>
    /// v1 carries mainnet's fork schedule only. Another chain needs a chainspec-driven spec
    /// provider, so rather than silently apply the wrong rules, engine creation refuses.
    /// </remarks>
    private const ulong SupportedChainId = BlockchainIds.Mainnet;

    [UnmanagedCallersOnly(EntryPoint = "nm_evm_abi_version")]
    public static uint AbiVersionExport() => AbiVersion;

    [UnmanagedCallersOnly(EntryPoint = "nm_evm_engine_new")]
    public static IntPtr EngineNew(NmEvmHost* host, ulong chainId)
    {
        try
        {
            if (host is null || host->GetAccount is null || host->GetStorage is null
                || host->GetCode is null || host->GetBlockHash is null) return IntPtr.Zero;
            if (chainId != SupportedChainId) return IntPtr.Zero;

            ILogManager logs = NullLogManager.Instance;
            ISpecProvider specs = MainnetSpecProvider.Instance;
            Diff diff = new();

            WorldState state = new(new HostScopeProvider(*host, diff), logs);
            EthereumTransactionProcessor processor = new(
                BlobBaseFeeCalculator.Instance, specs, state,
                new EthereumVirtualMachine(new HostBlockhashProvider(*host), specs, logs),
                new EthereumCodeInfoRepository(state), logs);

            Engine engine = new()
            {
                Processor = processor,
                State = state,
                Scope = state.BeginScope(null),
                Specs = specs,
                Diff = diff,
                Tracer = new ResultTracer(),
                ChainId = chainId,
            };
            return GCHandle.ToIntPtr(GCHandle.Alloc(engine));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "nm_evm_engine_free")]
    public static void EngineFree(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        GCHandle h = GCHandle.FromIntPtr(handle);
        (h.Target as Engine)?.Scope.Dispose();
        h.Free();
    }

    [UnmanagedCallersOnly(EntryPoint = "nm_evm_set_block")]
    public static int SetBlock(IntPtr handle, NmEvmBlock* block)
    {
        Engine? engine = Resolve(handle);
        if (engine is null) return NmStatus.Engine;
        if (block is null) return NmStatus.Argument;
        try
        {
            BlockHeader header = new(
                Keccak.Zero,
                Keccak.OfAnEmptySequenceRlp,
                new Address(new ReadOnlySpan<byte>(block->Coinbase, 20).ToArray()),
                UInt256.Zero,
                block->Number,
                block->GasLimit,
                block->Timestamp,
                [])
            {
                StateRoot = Keccak.EmptyTreeHash,
                BaseFeePerGas = new UInt256(new ReadOnlySpan<byte>(block->BaseFee, 32)),
                MixHash = new Hash256(new ReadOnlySpan<byte>(block->PrevRandao, 32)),
                ExcessBlobGas = block->HasExcessBlobGas != 0 ? block->ExcessBlobGas : null,
                BlobGasUsed = 0,
            };
            engine.Header = header;
            engine.Processor.SetBlockExecutionContext(header);
            return NmStatus.Ok;
        }
        catch (Exception e)
        {
            engine.LastError = e.ToString();
            return NmStatus.Internal;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "nm_evm_execute")]
    public static int Execute(IntPtr handle, NmEvmTx* tx, uint flags, NmEvmResult* result)
    {
        Engine? engine = Resolve(handle);
        if (engine is null) return NmStatus.Engine;
        if (tx is null || result is null) return NmStatus.Argument;
        if (engine.Header is null) return NmStatus.NoBlock;

        try
        {
            engine.Diff.Clear();
            engine.Tracer.Reset();

            Transaction transaction = BuildTransaction(tx, engine.ChainId);
            ExecutionOptions options = (flags & 1u) != 0
                ? ExecutionOptions.SkipValidationAndCommit
                : ExecutionOptions.Commit;

            TransactionResult outcome = engine.Processor.Process(transaction, engine.Tracer, options);
            if (!outcome)
            {
                engine.LastError = outcome.ToString();
                return NmStatus.TxRejected;
            }

            // The processor commits with commitRoots:false once EIP-658 is in force
            // (TransactionProcessor.cs:676), which leaves the changes in the state provider and
            // never opens a write batch. The host wants them per transaction, so flush here: the
            // scope computes no roots, making this the cost of walking the changed accounts.
            engine.State.Commit(engine.Specs.GetSpec(engine.Header), NullStateTracer.Instance,
                isGenesis: false, commitRoots: true);

            Marshalling.Write(engine, result);
            return NmStatus.Ok;
        }
        catch (Exception e)
        {
            engine.LastError = e.ToString();
            return NmStatus.Internal;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "nm_evm_result_free")]
    public static void ResultFree(NmEvmResult* result)
    {
        if (result is null || result->Arena is null) return;
        NativeMemory.Free(result->Arena);
        result->Arena = null;
    }

    /// <summary>The Nethermind revision this EVM was compiled from.</summary>
    /// <remarks>
    /// A host that also runs a Nethermind node should compare this against the node's own commit
    /// and refuse to build on a mismatch: two EVMs from different revisions can disagree, and the
    /// disagreement surfaces as an invalid block rather than an error.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "nm_evm_build_info")]
    public static int BuildInfoExport(byte* buffer, int bufferLen)
    {
        string info = $"nethermind-evm {BuildInfo.Version} commit={BuildInfo.Commit}"
                      + (BuildInfo.Dirty ? "-dirty" : "") + $" abi={AbiVersion}";
        byte[] utf8 = Encoding.UTF8.GetBytes(info);
        if (buffer is null || bufferLen <= utf8.Length) return utf8.Length;
        utf8.CopyTo(new Span<byte>(buffer, bufferLen));
        buffer[utf8.Length] = 0;
        return utf8.Length;
    }

    [UnmanagedCallersOnly(EntryPoint = "nm_evm_last_error")]
    public static int LastError(IntPtr handle, byte* buffer, int bufferLen)
    {
        Engine? engine = Resolve(handle);
        if (engine is null) return NmStatus.Engine;

        byte[] utf8 = Encoding.UTF8.GetBytes(engine.LastError ?? "");
        if (buffer is null || bufferLen <= utf8.Length) return utf8.Length;
        utf8.CopyTo(new Span<byte>(buffer, bufferLen));
        buffer[utf8.Length] = 0;
        return utf8.Length;
    }

    private static Engine? Resolve(IntPtr handle) =>
        handle == IntPtr.Zero ? null : GCHandle.FromIntPtr(handle).Target as Engine;

    private static Transaction BuildTransaction(NmEvmTx* tx, ulong chainId)
    {
        TxType type = (TxType)tx->TxType;
        bool supports1559 = type >= TxType.EIP1559;

        UInt256 maxFee = new(new ReadOnlySpan<byte>(tx->MaxFeePerGas, 32));
        UInt256 maxPriority = new(new ReadOnlySpan<byte>(tx->MaxPriorityFeePerGas, 32));

        return new Transaction
        {
            Type = type,
            ChainId = type == TxType.Legacy ? null : chainId,
            Nonce = tx->Nonce,
            GasLimit = tx->GasLimit,
            // Nethermind keeps the priority fee in GasPrice for 1559-style envelopes, and the
            // plain gas price there for a legacy one.
            GasPrice = supports1559 ? maxPriority : maxFee,
            DecodedMaxFeePerGas = supports1559 ? maxFee : UInt256.Zero,
            Value = new UInt256(new ReadOnlySpan<byte>(tx->Value, 32)),
            SenderAddress = new Address(new ReadOnlySpan<byte>(tx->Sender, 20).ToArray()),
            To = tx->HasTo != 0 ? new Address(new ReadOnlySpan<byte>(tx->To, 20).ToArray()) : null,
            Data = tx->DataLen > 0 ? new ReadOnlySpan<byte>(tx->Data, tx->DataLen).ToArray() : null,
        };
    }
}
