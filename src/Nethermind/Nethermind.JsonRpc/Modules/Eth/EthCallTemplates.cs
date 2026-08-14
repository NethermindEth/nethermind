// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>
/// Learns "guarded call templates" for <c>eth_call</c> requests whose result is a pure function of one
/// argument-keyed mapping slot (e.g. ERC-20 <c>balanceOf</c>), then serves such calls by a direct storage
/// read instead of EVM execution.
/// </summary>
/// <remarks>
/// A template for a (to, selector) pair is derived from two recorded executions with different arguments:
/// both must perform the same storage-read sequence except for exactly one slot, whose position must equal
/// <c>keccak(pad32(arg) ++ pad32(k))</c> for the same mapping index <c>k</c> in both traces, and whose value
/// must equal the 32-byte call output. All other reads become value-guards that are re-checked against the
/// requested block before a template answer is served; any guard or code-hash change invalidates the entry.
/// Recording runs as a dedicated extra execution (at most twice per pair) so the user's request always stays
/// on the normal path while learning. In shadow mode the normal path also runs on template hits and its
/// result is served; the template answer is only compared and mismatches blacklist the entry.
/// Thread-safe: the store is a bounded <see cref="LruCache{TKey,TValue}"/> holding immutable entries.
/// </remarks>
public sealed class EthCallTemplates(
    IShareableTxProcessorSource txProcessorSource,
    IStateReader stateReader,
    ISpecProvider specProvider,
    IJsonRpcConfig rpcConfig)
{
    private const int StoreCapacity = 4096;
    private const int MaxGuards = 8;
    private const int MaxReads = 64;
    private const int MaxMappingSlotIndex = 255;
    private const int WordSize = 32;
    private const int SelectorSize = 4;
    private const int QualifyingCallDataLength = SelectorSize + WordSize;

    private readonly LruCache<TemplateKey, Entry> _store = new(StoreCapacity, "eth_call templates");
    private readonly bool _shadowMode = rpcConfig.EthCallTemplatesShadowMode;

    private long _derived;
    private long _blacklisted;
    private long _hits;
    private long _shadowMatches;
    private long _shadowMismatches;
    private long _guardInvalidations;

    /// <summary>Number of templates derived by this instance.</summary>
    public long TemplatesDerived => Volatile.Read(ref _derived);
    /// <summary>Number of (to, selector) pairs blacklisted by this instance.</summary>
    public long TemplatesBlacklisted => Volatile.Read(ref _blacklisted);
    /// <summary>Number of calls answered directly from a template (non-shadow mode only).</summary>
    public long TemplateHits => Volatile.Read(ref _hits);
    /// <summary>Number of shadow-mode template answers that matched the EVM result.</summary>
    public long TemplateShadowMatches => Volatile.Read(ref _shadowMatches);
    /// <summary>Number of shadow-mode template answers that diverged from the EVM result.</summary>
    public long TemplateShadowMismatches => Volatile.Read(ref _shadowMismatches);
    /// <summary>Number of template entries invalidated because a guard or code hash changed.</summary>
    public long GuardInvalidations => Volatile.Read(ref _guardInvalidations);

    /// <summary>Creates the engine when <see cref="IJsonRpcConfig.EthCallTemplates"/> is enabled, otherwise <c>null</c>.</summary>
    public static EthCallTemplates? CreateIfEnabled(
        IJsonRpcConfig config,
        IShareableTxProcessorSource txProcessorSource,
        IStateReader stateReader,
        ISpecProvider specProvider) =>
        config.EthCallTemplates ? new EthCallTemplates(txProcessorSource, stateReader, specProvider, config) : null;

    /// <summary>Executes an <c>eth_call</c> request, serving or learning a template when the call qualifies.</summary>
    /// <param name="call">The call request; only single-word-argument calls to a contract with zero value qualify.</param>
    /// <param name="header">The resolved concrete block header the call executes against.</param>
    /// <param name="executeCall">The normal execution path, invoked at most once.</param>
    public ResultWrapper<HexBytes> Execute(TransactionForRpc call, BlockHeader header, Func<ResultWrapper<HexBytes>> executeCall)
    {
        if (!TryQualify(call, out Address? to, out uint selector, out UInt256 arg))
        {
            return executeCall();
        }

        TemplateKey key = new(to, selector);
        _store.TryGet(key, out Entry? entry);

        if (entry is Blacklisted)
        {
            return executeCall();
        }

        if (entry is Templated templated)
        {
            if (TryReadThroughTemplate(templated.Template, header, arg, out byte[]? templateOutput))
            {
                return ServeTemplate(key, templateOutput, executeCall);
            }

            Interlocked.Increment(ref _guardInvalidations);
            _store.Delete(key);
            entry = null;
        }

        ResultWrapper<HexBytes> result = executeCall();
        Learn(call, header, key, arg, (entry as FirstTrace)?.Trace, result);
        return result;
    }

    private ResultWrapper<HexBytes> ServeTemplate(in TemplateKey key, byte[] templateOutput, Func<ResultWrapper<HexBytes>> executeCall)
    {
        if (!_shadowMode)
        {
            Interlocked.Increment(ref _hits);
            Interlocked.Increment(ref Metrics.EthCallTemplateHits);
            return ResultWrapper<HexBytes>.Success(new HexBytes(templateOutput));
        }

        ResultWrapper<HexBytes> normal = executeCall();
        if (normal.Result.ResultType == ResultType.Success)
        {
            if (normal.Data.Bytes.Span.SequenceEqual(templateOutput))
            {
                Interlocked.Increment(ref _shadowMatches);
                Interlocked.Increment(ref Metrics.EthCallTemplateShadowMatches);
            }
            else
            {
                ReportShadowMismatch(key);
            }
        }
        else if (normal.ErrorCode == ErrorCodes.ExecutionReverted)
        {
            // A guarded template can never revert, so a reverting EVM result is a divergence.
            // Other failures (timeout, unavailable state) say nothing about the template.
            ReportShadowMismatch(key);
        }

        return normal;
    }

    private void ReportShadowMismatch(in TemplateKey key)
    {
        Interlocked.Increment(ref _shadowMismatches);
        Interlocked.Increment(ref Metrics.EthCallTemplateShadowMismatches);
        Blacklist(key);
    }

    private void Learn(TransactionForRpc call, BlockHeader header, in TemplateKey key, in UInt256 arg, CallTrace? firstTrace, ResultWrapper<HexBytes> result)
    {
        // Only deterministic 32-byte successes fit the single-mapping-read model.
        if (result.Result.ResultType != ResultType.Success || result.Data.Bytes.Length != WordSize)
        {
            return;
        }

        // Derivation needs two observations with different arguments.
        if (firstTrace is not null && firstTrace.Arg == arg)
        {
            return;
        }

        CallTrace? trace = RecordTrace(call, header, key.To.Value, arg);
        // A recorded output diverging from the normal path signals recording-path drift — never template such a call.
        if (trace is null || trace.Output != new UInt256(result.Data.Bytes.Span, isBigEndian: true))
        {
            Blacklist(key);
            return;
        }

        if (firstTrace is null)
        {
            _store.Set(key, new FirstTrace(trace));
            return;
        }

        Template? template = TryDerive(firstTrace, trace, key.To.Value);
        if (template is null)
        {
            Blacklist(key);
            return;
        }

        _store.Set(key, new Templated(template));
        Interlocked.Increment(ref _derived);
        Interlocked.Increment(ref Metrics.EthCallTemplatesDerived);
    }

    private void Blacklist(in TemplateKey key)
    {
        _store.Set(key, Blacklisted.Instance);
        Interlocked.Increment(ref _blacklisted);
        Interlocked.Increment(ref Metrics.EthCallTemplatesBlacklisted);
    }

    private static bool TryQualify(TransactionForRpc call, [NotNullWhen(true)] out Address? to, out uint selector, out UInt256 arg)
    {
        to = null;
        selector = 0;
        arg = default;

        if (call is not LegacyTransactionForRpc legacy
            || legacy.To is null
            || legacy.Value is { IsZero: false }
            || legacy.Input is not { Length: QualifyingCallDataLength } input)
        {
            return false;
        }

        to = legacy.To;
        selector = BinaryPrimitives.ReadUInt32BigEndian(input);
        arg = new UInt256(input.AsSpan(SelectorSize, WordSize), isBigEndian: true);
        return true;
    }

    private bool TryReadThroughTemplate(Template template, BlockHeader header, in UInt256 arg, [NotNullWhen(true)] out byte[]? output)
    {
        output = null;

        if (!stateReader.TryGetAccount(header, template.To, out AccountStruct account) || account.CodeHash != template.CodeHash)
        {
            return false;
        }

        foreach (StorageRead guard in template.Guards)
        {
            if (ReadStorage(header, guard.Address.Value, guard.Index) != guard.Value)
            {
                return false;
            }
        }

        UInt256 slot = ComputeMappingSlot(arg, template.MappingSlotIndex);
        output = ReadStorage(header, template.StorageAddress, slot).ToBigEndian();
        return true;
    }

    private UInt256 ReadStorage(BlockHeader header, Address address, in UInt256 index) =>
        new(stateReader.GetStorage(header, address, index), isBigEndian: true);

    /// <summary>Computes the Solidity mapping slot <c>keccak(pad32(arg) ++ pad32(slotIndex))</c>.</summary>
    private static UInt256 ComputeMappingSlot(in UInt256 arg, byte slotIndex)
    {
        Span<byte> material = stackalloc byte[2 * WordSize];
        arg.ToBigEndian(material[..WordSize]);
        material[WordSize..].Clear();
        material[2 * WordSize - 1] = slotIndex;
        return new UInt256(ValueKeccak.Compute(material).Bytes, isBigEndian: true);
    }

    private static int FindMappingSlotIndex(in UInt256 arg, in UInt256 slot)
    {
        for (int k = 0; k <= MaxMappingSlotIndex; k++)
        {
            if (ComputeMappingSlot(arg, (byte)k) == slot)
            {
                return k;
            }
        }

        return -1;
    }

    private static Template? TryDerive(CallTrace first, CallTrace second, Address to)
    {
        if (first.CodeHash != second.CodeHash)
        {
            return null;
        }

        StorageRead[] firstReads = first.Reads;
        StorageRead[] secondReads = second.Reads;
        if (firstReads.Length != secondReads.Length || firstReads.Length == 0 || firstReads.Length - 1 > MaxGuards)
        {
            return null;
        }

        int diffPosition = -1;
        for (int i = 0; i < firstReads.Length; i++)
        {
            if (firstReads[i].Address.Equals(secondReads[i].Address) && firstReads[i].Index == secondReads[i].Index)
            {
                continue;
            }

            if (!firstReads[i].Address.Equals(secondReads[i].Address) || diffPosition >= 0)
            {
                return null;
            }

            diffPosition = i;
        }

        if (diffPosition < 0)
        {
            return null;
        }

        StorageRead firstDiff = firstReads[diffPosition];
        StorageRead secondDiff = secondReads[diffPosition];
        if (first.Output != firstDiff.Value || second.Output != secondDiff.Value)
        {
            return null;
        }

        int slotIndex = FindMappingSlotIndex(first.Arg, firstDiff.Index);
        if (slotIndex < 0 || ComputeMappingSlot(second.Arg, (byte)slotIndex) != secondDiff.Index)
        {
            return null;
        }

        StorageRead[] guards = new StorageRead[secondReads.Length - 1];
        int guardCount = 0;
        for (int i = 0; i < secondReads.Length; i++)
        {
            if (i != diffPosition)
            {
                guards[guardCount++] = secondReads[i];
            }
        }

        return new Template
        {
            To = to,
            CodeHash = second.CodeHash,
            StorageAddress = secondDiff.Address.Value,
            MappingSlotIndex = (byte)slotIndex,
            Guards = guards,
        };
    }

    /// <summary>Runs a dedicated recording execution of the call, mirroring the shareable-scope bridge path.</summary>
    /// <returns>The trace, or <c>null</c> when the call cannot be recorded faithfully (side effects, failure, overflow).</returns>
    private CallTrace? RecordTrace(TransactionForRpc call, BlockHeader header, Address to, in UInt256 arg)
    {
        IReleaseSpec spec = specProvider.GetSpec(header);
        Result<Transaction> prepared = call.ToTransaction(validateUserInput: true, gasCap: rpcConfig.GasCap, spec: spec);
        if (!prepared.Success(out Transaction? tx, out _))
        {
            return null;
        }

        tx.ChainId = specProvider.ChainId;

        // Mirrors TxExecutor.Execute + BlockchainBridge.CallAndRestore header preparation for
        // the eth_call case (no overrides, treatBlockHeaderAsParentBlock: false).
        BlockHeader callHeader = header.Clone();
        if (!call.ShouldSetBaseFee())
        {
            callHeader.BaseFeePerGas = 0;
        }

        callHeader.GasUsed = 0;
        tx.SenderAddress ??= Address.Zero;
        tx.Nonce = stateReader.GetNonce(callHeader, tx.SenderAddress);

        UInt256 blobBaseFee = UInt256.Zero;
        if (spec.IsEip4844Enabled)
        {
            callHeader.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(tx);
            BlobGasCalculator.TryCalculateFeePerBlobGas(callHeader, spec.BlobBaseFeeUpdateFraction, out blobBaseFee);
            if (tx.Type is TxType.Blob && tx.MaxFeePerBlobGas is null)
            {
                tx.MaxFeePerBlobGas = blobBaseFee;
            }
        }

        callHeader.IsPostMerge = header.Difficulty == 0;
        tx.Hash = tx.CalculateHash();
        BlockExecutionContext blockExecutionContext = new(callHeader, spec, blobBaseFee);

        RecordingTxTracer tracer = new();
        try
        {
            using CancellationTokenSource timeout = rpcConfig.BuildTimeoutCancellationToken();
            using IReadOnlyTxProcessingScope processingScope = txProcessorSource.Build(header);
            TransactionResult transactionResult = processingScope.TransactionProcessor.CallAndRestore(
                tx, in blockExecutionContext, tracer.WithCancellation(timeout.Token));
            if (!transactionResult.TransactionExecuted)
            {
                return null;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or InsufficientBalanceException)
        {
            return null;
        }

        if (!tracer.Success || tracer.HasSideEffects || tracer.ReadOverflow || tracer.Output is not { Length: WordSize } output)
        {
            return null;
        }

        if (!stateReader.TryGetAccount(header, to, out AccountStruct account))
        {
            return null;
        }

        return new CallTrace
        {
            Arg = arg,
            CodeHash = account.CodeHash,
            Reads = tracer.Reads.ToArray(),
            Output = new UInt256(output, isBigEndian: true),
        };
    }

    private readonly record struct TemplateKey(AddressAsKey To, uint Selector);

    private readonly record struct StorageRead(AddressAsKey Address, UInt256 Index, UInt256 Value);

    private abstract class Entry;

    private sealed class FirstTrace(CallTrace trace) : Entry
    {
        public CallTrace Trace { get; } = trace;
    }

    private sealed class Templated(Template template) : Entry
    {
        public Template Template { get; } = template;
    }

    private sealed class Blacklisted : Entry
    {
        public static readonly Blacklisted Instance = new();
    }

    private sealed class CallTrace
    {
        public required UInt256 Arg { get; init; }
        public required ValueHash256 CodeHash { get; init; }
        public required StorageRead[] Reads { get; init; }
        public required UInt256 Output { get; init; }
    }

    private sealed class Template
    {
        public required Address To { get; init; }
        public required ValueHash256 CodeHash { get; init; }
        public required Address StorageAddress { get; init; }
        public required byte MappingSlotIndex { get; init; }
        public required StorageRead[] Guards { get; init; }
    }

    private sealed class RecordingTxTracer : TxTracer
    {
        public List<StorageRead> Reads { get; } = [];
        public byte[]? Output { get; private set; }
        public bool Success { get; private set; }
        public bool HasSideEffects { get; private set; }
        public bool ReadOverflow { get; private set; }

        public override bool IsTracingReceipt => true;
        public override bool IsTracingOpLevelStorage => true;

        public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
        {
            if (Reads.Count >= MaxReads)
            {
                ReadOverflow = true;
                return;
            }

            Reads.Add(new StorageRead(address, storageIndex, new UInt256(value, isBigEndian: true)));
        }

        public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
            => HasSideEffects = true;

        // Transient storage cannot be value-guarded across requests, so its use disqualifies the call.
        public override void LoadOperationTransientStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
            => HasSideEffects = true;

        public override void SetOperationTransientStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
            => HasSideEffects = true;

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            Success = true;
            Output = output;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
            => Success = false;
    }
}
