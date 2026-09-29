// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <inheritdoc cref="IProfile2EligibilityReplayer"/>
/// <remarks>
/// EIP-8369 § Attesters: state at the evaluation index is the parent state plus the EIP-7928 access list's
/// changes before that index, pre-execution system updates included. The changes are written into a
/// throwaway env opened at the parent, then the validation prefix runs under the Profile 2 trace rules.
/// <see cref="BlockAccessListBasedWorldState"/> is not reused: it rejects every account the list omits,
/// and an omitted transaction's sender and payer usually are such accounts.
/// <para>The prefix runs under EIP-8141's <c>MAX_VERIFY_GAS</c>, which is below
/// <see cref="Eip8369Constants.MaxVerifyGasPerTx"/>, so a prefix spending more is judged ineligible.</para>
/// </remarks>
public sealed class Profile2EligibilityReplayer(
    IReadOnlyTxProcessingEnvFactory envFactory,
    IEthereumEcdsa ecdsa,
    ILogManager logManager) : IProfile2EligibilityReplayer, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<Profile2EligibilityReplayer>();
    private readonly Lock _lock = new();
    private IReadOnlyTxProcessorSource? _source;
    private bool _disposed;

    public bool AreSignaturesValid(Transaction transaction, IReleaseSpec spec) => AreSignaturesValid(transaction, ecdsa, spec);

    /// <inheritdoc cref="IProfile2EligibilityReplayer.AreSignaturesValid"/>
    public static bool AreSignaturesValid(Transaction transaction, IEthereumEcdsa ecdsa, IReleaseSpec spec)
    {
        if (transaction.FrameSignatures is not { Length: > 0 }) return true;

        IPrecompile? p256Precompile = spec.IsPrecompile(FrameTxSignatureValidator.P256VerifyPrecompileAddress)
            ? SecP256r1Precompile.Instance
            : null;
        return FrameTxSignatureValidator.Validate(transaction, ecdsa, p256Precompile, spec, out _);
    }

    public bool IsEligible(Block block, Transaction transaction, int index, IReleaseSpec spec)
    {
        if (transaction.SenderAddress is not { } sender) return false;
        if (block.BlockAccessList is not { } blockAccessList) return Undecided(transaction, "the block carries no access list");

        lock (_lock)
        {
            if (_disposed) return Undecided(transaction, "the replayer is disposed");

            FrameTxValidationTracer? tracer = null;
            try
            {
                IReadOnlyTxProcessorSource source = _source ??= envFactory.Create();
                if (!source.TryBuildAtTarget(block.Header, out IReadOnlyTxProcessingScope? scope))
                    return Undecided(transaction, "the parent state is unavailable");

                using (scope)
                {
                    IWorldState state = scope.WorldState;
                    ApplyChangesBefore(state, blockAccessList, (uint)index + 1, spec);
                    if (!IsNonceValid(state, transaction, sender)) return false;

                    if (!AreRecentRootsValid(state, transaction, block.Header, spec)) return false;

                    Address payer = FrameTxValidation.GetPrefixPaymaster(transaction) ?? sender;
                    tracer = new FrameTxValidationTracer(sender, Eip8141Constants.ExpiryVerifierAddress, state, spec,
                        payer: payer, storageSlotBound: Eip8369Constants.AaVopsSlotCount);

                    ITransactionProcessor processor = scope.TransactionProcessor;
                    processor.SetBlockExecutionContext(block.Header);
                    TransactionResult result = processor.Process(transaction, tracer, ExecutionOptions.FrameValidationPrefixOnly);
                    return !tracer.Violated && result && tracer.Payer is not null;
                }
            }
            catch (OperationCanceledException) when (tracer is { Violated: true })
            {
                return false;
            }
            catch (Exception e) when (e is IInternalNethermindException or StateUnavailableException or ObjectDisposedException or IOException)
            {
                return Undecided(transaction, e.Message);
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException && tracer is not null)
            {
                // Once the tracer exists the prefix is the expected source of a throw, as in FrameTxPrefixSimulator.
                if (_logger.IsDebug) _logger.Debug($"EIP-8369 replay of {transaction.Hash} threw; judging it ineligible. {e}");
                return false;
            }
        }
    }

    /// <summary>Writes every change <paramref name="blockAccessList"/> records before <paramref name="blockAccessIndex"/>.</summary>
    /// <remarks>Anything the list omits is unchanged by the block, so the parent state already holds it.</remarks>
    private static void ApplyChangesBefore(IWorldState state, ReadOnlyBlockAccessList blockAccessList, uint blockAccessIndex, IReleaseSpec spec)
    {
        foreach (ReadOnlyAccountChanges changes in blockAccessList.AccountChanges.AsSpan())
        {
            Address address = changes.Address;
            bool hasBalance = changes.TryGetLastBalanceChangeBefore(blockAccessIndex, out BalanceChange balance);
            bool hasNonce = changes.TryGetLastNonceChangeBefore(blockAccessIndex, out NonceChange nonce);
            bool hasCode = changes.TryGetLastCodeChangeBefore(blockAccessIndex, out CodeChange code);
            bool created = false;

            if (hasBalance)
            {
                created = EnsureAccount(state, address, created);
                UInt256 current = state.GetBalance(address);
                if (balance.Value > current) state.AddToBalance(address, balance.Value - current, spec, out _);
                else if (balance.Value < current) state.SubtractFromBalance(address, current - balance.Value, spec, out _);
            }

            if (hasNonce)
            {
                created = EnsureAccount(state, address, created);
                state.SetNonce(address, nonce.Value);
            }

            if (hasCode)
            {
                created = EnsureAccount(state, address, created);
                state.InsertCode(address, code.CodeHash, code.Code ?? [], spec);
            }

            foreach (ReadOnlySlotChanges slot in changes.StorageChanges)
            {
                if (!slot.TryGetLastBefore(blockAccessIndex, out StorageChange storage)) continue;
                created = EnsureAccount(state, address, created);
                state.Set(new StorageCell(address, slot.Key), storage.Value);
            }
        }
    }

    private static bool EnsureAccount(IWorldState state, Address address, bool created)
    {
        if (!created) state.CreateAccountIfNotExists(address, UInt256.Zero);
        return true;
    }

    /// <summary>The EIP-8141 account nonce or EIP-8250 keyed nonce check, which prefix simulation leaves to its caller.</summary>
    private static bool IsNonceValid(IWorldState state, Transaction transaction, Address sender) =>
        transaction.NonceKeys is { } nonceKeys
            ? KeyedNonceManager.IsNonceSetValid(state, sender, nonceKeys, transaction.Nonce)
            : state.GetNonce(sender) == transaction.Nonce;

    /// <remarks>current_slot is the block's own slot (EIP-8369 § Attesters). The prefix simulation anchors one
    /// slot later, as mempool admission does, so the oldest usable root is also judged ineligible.</remarks>
    private static bool AreRecentRootsValid(IWorldState state, Transaction transaction, BlockHeader header, IReleaseSpec spec)
    {
        if (transaction.RecentRootReferences is null) return true;
        if (!spec.IsEip8272Enabled) return false;

        using StackAccessTracker accessTracker = new();
        return RecentRootReferences.Validate(state, transaction.RecentRootReferences, header.SlotNumber, in accessTracker);
    }

    private bool Undecided(Transaction transaction, string reason)
    {
        if (_logger.IsDebug) _logger.Debug($"EIP-8369 replay of {transaction.Hash} undecided, enforcing its omission: {reason}.");
        return true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _source?.Dispose();
            _source = null;
        }
    }
}
