// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
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
/// A template for a (to, selector) pair is derived from two recorded executions with different arguments and
/// different outputs: both must perform the same storage-read sequence except for exactly one slot, whose
/// position must equal <c>keccak(pad32(arg) ++ pad32(k))</c> for the same mapping index <c>k</c> in both
/// traces, and whose value must equal the 32-byte call output. All other reads become value-guards that are
/// re-checked against the requested block before a template answer is served; a guard or code-hash change at
/// the current head invalidates the entry (historical queries just fall through). Recording runs as a
/// dedicated extra execution so the user's request always stays on the normal path while learning: one
/// recording per learning observation, at most one in flight per pair, and a head-block guard invalidation
/// starts a new cycle. An equal-output candidate pair is ambiguous rather than disqualifying (e.g. two
/// zero balances) — the fresher trace is kept and derivation is retried, up to
/// <see cref="MaxDerivationAttempts"/> failures before the pair is blacklisted. In shadow mode the normal
/// path also runs on template hits and its result is served; the template answer is only compared and
/// mismatches blacklist the entry.
/// Thread-safe: the store is a bounded <see cref="LruCache{TKey,TValue}"/> holding immutable entries.
/// </remarks>
public sealed class EthCallTemplates(
    IShareableTxProcessorSource txProcessorSource,
    IStateReader stateReader,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    IJsonRpcConfig rpcConfig)
{
    private const int StoreCapacity = 4096;
    private const int MaxGuards = 8;
    private const int MaxReads = 64;
    private const int MaxMappingSlotIndex = 255;
    private const int WordSize = 32;
    private const int SelectorSize = 4;
    private const int QualifyingCallDataLength = SelectorSize + WordSize;
    private const ulong MinQualifyingGasLimit = 100_000;
    private const int MaxDerivationAttempts = 16;

    private readonly LruCache<TemplateKey, Entry> _store = new(StoreCapacity, "eth_call templates");
    private readonly ConcurrentDictionary<TemplateKey, bool> _learningInFlight = new();
    private readonly bool _shadowMode = rpcConfig.EthCallTemplatesShadowMode;

    /// <summary>Creates the engine when <see cref="IJsonRpcConfig.EthCallTemplates"/> is enabled, otherwise <c>null</c>.</summary>
    public static EthCallTemplates? CreateIfEnabled(
        IJsonRpcConfig config,
        IShareableTxProcessorSource txProcessorSource,
        IStateReader stateReader,
        ISpecProvider specProvider,
        IBlockTree blockTree) =>
        config.EthCallTemplates ? new EthCallTemplates(txProcessorSource, stateReader, specProvider, blockTree, config) : null;

    /// <summary>Executes an <c>eth_call</c> request, serving or learning a template when the call qualifies.</summary>
    /// <param name="call">The call request; only single-word-argument calls to a contract with zero value qualify.</param>
    /// <param name="header">The resolved concrete block header the call executes against.</param>
    /// <param name="executeCall">The normal execution path, invoked at most once.</param>
    public ResultWrapper<HexBytes> Execute(TransactionForRpc call, BlockHeader header, Func<ResultWrapper<HexBytes>> executeCall)
    {
        // The state check must precede any template state read: the normal path reports missing state
        // (pruned historical blocks) as a clean ResourceUnavailable, which template reads must not preempt.
        if (!TryQualify(call, out Address? to, out uint selector, out UInt256 arg) || !stateReader.HasStateForBlock(header))
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

            // Guards legitimately mismatch on historical blocks; only a mismatch at the current head
            // means the template is stale, so historical queries never invalidate or relearn.
            if (blockTree.Head?.Hash != header.Hash)
            {
                return executeCall();
            }

            Interlocked.Increment(ref Metrics.EthCallTemplateGuardInvalidations);
            _store.Delete(key);
            entry = null;
        }

        // Started before the normal execution so learning shares the request's time budget instead of
        // getting a fresh full-length one after the call already ran.
        using CancellationTokenSource timeout = rpcConfig.BuildTimeoutCancellationToken();
        ResultWrapper<HexBytes> result = executeCall();
        Learn(call, header, key, arg, entry as FirstTrace, result, timeout.Token);
        return result;
    }

    private ResultWrapper<HexBytes> ServeTemplate(in TemplateKey key, byte[] templateOutput, Func<ResultWrapper<HexBytes>> executeCall)
    {
        if (!_shadowMode)
        {
            Interlocked.Increment(ref Metrics.EthCallTemplateHits);
            return ResultWrapper<HexBytes>.Success(new HexBytes(templateOutput));
        }

        ResultWrapper<HexBytes> normal = executeCall();
        if (normal.Result.ResultType == ResultType.Success)
        {
            if (normal.Data.Bytes.Span.SequenceEqual(templateOutput))
            {
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
        Interlocked.Increment(ref Metrics.EthCallTemplateShadowMismatches);
        Blacklist(key);
    }

    private void Learn(TransactionForRpc call, BlockHeader header, in TemplateKey key, in UInt256 arg, FirstTrace? firstEntry, ResultWrapper<HexBytes> result, CancellationToken cancellationToken)
    {
        // Only deterministic 32-byte successes fit the single-mapping-read model.
        if (result.Result.ResultType != ResultType.Success || result.Data.Bytes.Length != WordSize)
        {
            return;
        }

        // Derivation needs two observations with different arguments.
        if (firstEntry is not null && firstEntry.Trace.Arg == arg)
        {
            return;
        }

        // Concurrent misses for the same pair would each pay a recording execution; only one learns at a time.
        if (!_learningInFlight.TryAdd(key, true))
        {
            return;
        }

        try
        {
            CallTrace? trace = RecordTrace(call, header, key.To.Value, arg, cancellationToken);
            // A recorded output diverging from the normal path signals recording-path drift — never template such a call.
            if (trace is null || trace.Output != new UInt256(result.Data.Bytes.Span, isBigEndian: true))
            {
                Blacklist(key);
                return;
            }

            // A concurrent shadow mismatch may have just blacklisted the pair; never resurrect it.
            if (IsBlacklisted(key))
            {
                return;
            }

            if (firstEntry is null)
            {
                _store.Set(key, new FirstTrace(trace, failedAttempts: 0));
                return;
            }

            switch (TryDerive(firstEntry.Trace, trace, key.To.Value, out Template? template))
            {
                case DerivationOutcome.Rejected:
                    Blacklist(key);
                    break;
                case DerivationOutcome.EqualOutputs when firstEntry.FailedAttempts + 1 >= MaxDerivationAttempts:
                    // A genuinely constant function keeps producing equal outputs; each retry costs a
                    // recorded execution, so give up after a bounded number of attempts.
                    Blacklist(key);
                    break;
                case DerivationOutcome.EqualOutputs:
                    // Keep the fresher of the two equally informative traces so learning is never
                    // pinned to guard values observed at an old block.
                    _store.Set(key, new FirstTrace(trace, firstEntry.FailedAttempts + 1));
                    break;
                case DerivationOutcome.Derived:
                    _store.Set(key, new Templated(template!));
                    Interlocked.Increment(ref Metrics.EthCallTemplatesDerived);
                    break;
            }
        }
        finally
        {
            _learningInFlight.TryRemove(key, out _);
        }
    }

    private bool IsBlacklisted(in TemplateKey key) => _store.TryGet(key, out Entry? current) && current is Blacklisted;

    private void Blacklist(in TemplateKey key)
    {
        _store.Set(key, Blacklisted.Instance);
        Interlocked.Increment(ref Metrics.EthCallTemplatesBlacklisted);
    }

    /// <summary>Decides whether a call is eligible for templating and extracts its (to, selector, arg) shape.</summary>
    /// <remarks>
    /// Beyond the 4-byte-selector + one-word-argument calldata shape, qualification is deliberately narrow:
    /// <list type="bullet">
    /// <item><c>From</c> must be absent — sender-dependent code paths (e.g. a whitelist read keyed by CALLER)
    /// would be learned as value-guards pinned to the learning sender and then served to other senders.</item>
    /// <item><c>Value</c>, <c>GasPrice</c>, <c>MaxFeePerGas</c>, <c>MaxPriorityFeePerGas</c> and <c>Nonce</c>
    /// must be absent or zero, and <c>Gas</c> absent or at least <see cref="MinQualifyingGasLimit"/>. This is a
    /// cheap conservative stand-in for the executor's input validation (intrinsic gas, fee payability), which a
    /// template hit skips; it also keeps GASPRICE/BASEFEE-visible inputs uniform between learning and serving.
    /// A deliberately low explicit gas limit in [<see cref="MinQualifyingGasLimit"/>, actual need) could still
    /// diverge (EVM out-of-gas vs template answer); the exposure is bounded by <see cref="MaxReads"/> and
    /// caught by shadow mode.</item>
    /// <item>Blob and set-code calls never qualify — versioned hashes and authorization lists carry semantics
    /// the template model does not observe.</item>
    /// </list>
    /// </remarks>
    private static bool TryQualify(TransactionForRpc call, [NotNullWhen(true)] out Address? to, out uint selector, out UInt256 arg)
    {
        to = null;
        selector = 0;
        arg = default;

        if (call is not LegacyTransactionForRpc legacy
            || call is BlobTransactionForRpc or SetCodeTransactionForRpc
            || legacy.To is null
            || legacy.From is not null
            || legacy.Nonce is > 0
            || legacy.Gas is < MinQualifyingGasLimit
            || legacy.Value is { IsZero: false }
            || legacy.GasPrice is { IsZero: false }
            || call is EIP1559TransactionForRpc { MaxFeePerGas.IsZero: false } or EIP1559TransactionForRpc { MaxPriorityFeePerGas.IsZero: false }
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

    private enum DerivationOutcome
    {
        Derived,
        /// <summary>Structurally incompatible traces; the pair can never be templated.</summary>
        Rejected,
        /// <summary>Structure and slot pattern fit but both outputs are equal (e.g. two zero balances) — ambiguous, retry with another argument.</summary>
        EqualOutputs,
    }

    private static DerivationOutcome TryDerive(CallTrace first, CallTrace second, Address to, out Template? template)
    {
        template = null;

        if (first.CodeHash != second.CodeHash)
        {
            return DerivationOutcome.Rejected;
        }

        StorageRead[] firstReads = first.Reads;
        StorageRead[] secondReads = second.Reads;
        if (firstReads.Length != secondReads.Length || firstReads.Length == 0 || firstReads.Length - 1 > MaxGuards)
        {
            return DerivationOutcome.Rejected;
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
                return DerivationOutcome.Rejected;
            }

            diffPosition = i;
        }

        if (diffPosition < 0)
        {
            return DerivationOutcome.Rejected;
        }

        StorageRead firstDiff = firstReads[diffPosition];
        StorageRead secondDiff = secondReads[diffPosition];
        if (first.Output != firstDiff.Value || second.Output != secondDiff.Value)
        {
            return DerivationOutcome.Rejected;
        }

        int slotIndex = FindMappingSlotIndex(first.Arg, firstDiff.Index);
        if (slotIndex < 0 || ComputeMappingSlot(second.Arg, (byte)slotIndex) != secondDiff.Index)
        {
            return DerivationOutcome.Rejected;
        }

        // Identical outputs (e.g. both zero) give no evidence the output tracks the differing slot,
        // but they are not evidence against it either — the caller retries with another argument.
        if (first.Output == second.Output)
        {
            return DerivationOutcome.EqualOutputs;
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

        template = new Template
        {
            To = to,
            CodeHash = second.CodeHash,
            StorageAddress = secondDiff.Address.Value,
            MappingSlotIndex = (byte)slotIndex,
            Guards = guards,
        };
        return DerivationOutcome.Derived;
    }

    /// <summary>Runs a dedicated recording execution of the call, mirroring the shareable-scope bridge path.</summary>
    /// <returns>The trace, or <c>null</c> when the call cannot be recorded faithfully (side effects, failure, overflow).</returns>
    private CallTrace? RecordTrace(TransactionForRpc call, BlockHeader header, Address to, in UInt256 arg, CancellationToken cancellationToken)
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
            using IReadOnlyTxProcessingScope processingScope = txProcessorSource.Build(header);
            TransactionResult transactionResult = processingScope.TransactionProcessor.CallAndRestore(
                tx, in blockExecutionContext, tracer.WithCancellation(cancellationToken));
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

    private sealed class FirstTrace(CallTrace trace, int failedAttempts) : Entry
    {
        public CallTrace Trace { get; } = trace;

        /// <summary>Number of derivation attempts against this pair that failed on equal outputs.</summary>
        public int FailedAttempts { get; } = failedAttempts;
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
