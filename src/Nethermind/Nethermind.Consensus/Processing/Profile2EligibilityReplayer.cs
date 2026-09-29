// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
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
/// <para>The prefix runs under <c>MAX_VERIFY_GAS_PER_TX</c> rather than EIP-8141's mempool <c>MAX_VERIFY_GAS</c>,
/// and its recent roots are checked at the block's own slot, as EIP-8369 § Attesters specifies.</para>
/// </remarks>
public sealed class Profile2EligibilityReplayer(
    IReadOnlyTxProcessingEnvFactory envFactory,
    IEthereumEcdsa ecdsa,
    IBlocksConfig blocksConfig,
    ILogManager logManager) : IProfile2EligibilityReplayer, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<Profile2EligibilityReplayer>();
    private readonly Lock _lock = new();
    private IReadOnlyTxProcessorSource? _source;

    // A verdict is a function of the envelope bytes the hash commits to, and of whether P256VERIFY exists, so the
    // builder's fill and the validation of the block it built share one verification per entry.
    private readonly LruCache<Hash256AsKey, bool> _signaturesWithP256 = new(SignatureCacheSize, "Profile2SignaturesWithP256");
    private readonly LruCache<Hash256AsKey, bool> _signaturesWithoutP256 = new(SignatureCacheSize, "Profile2SignaturesWithoutP256");
    private const int SignatureCacheSize = 4096;
    private bool _disposed;

    public bool AreSignaturesValid(Transaction transaction, IReleaseSpec spec)
    {
        if (transaction.FrameSignatures is not { Length: > 0 }) return true;
        if (transaction.Hash is not { } hash) return AreSignaturesValid(transaction, ecdsa, spec);

        LruCache<Hash256AsKey, bool> cache = spec.IsPrecompile(FrameTxSignatureValidator.P256VerifyPrecompileAddress)
            ? _signaturesWithP256
            : _signaturesWithoutP256;
        if (cache.TryGet(hash, out bool valid)) return valid;

        valid = AreSignaturesValid(transaction, ecdsa, spec);
        cache.Set(hash, valid);
        return valid;
    }

    /// <inheritdoc cref="IProfile2EligibilityReplayer.AreSignaturesValid"/>
    public static bool AreSignaturesValid(Transaction transaction, IEthereumEcdsa ecdsa, IReleaseSpec spec)
    {
        if (transaction.FrameSignatures is not { Length: > 0 }) return true;

        IPrecompile? p256Precompile = spec.IsPrecompile(FrameTxSignatureValidator.P256VerifyPrecompileAddress)
            ? SecP256r1Precompile.Instance
            : null;
        return FrameTxSignatureValidator.Validate(transaction, ecdsa, p256Precompile, spec, out _);
    }

    public bool[] AreEligible(Block block, IReadOnlyList<(Transaction Transaction, int Index)> requests, IReleaseSpec spec)
    {
        bool[] eligible = new bool[requests.Count];
        if (requests.Count == 0) return eligible;
        if (block.BlockAccessList is not { } blockAccessList) return Undecided(eligible, 0, "the block carries no access list");

        // Ascending index, so the state advances through the access list once for the whole batch.
        int[] order = new int[requests.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (x, y) => requests[x].Index != requests[y].Index ? requests[x].Index.CompareTo(requests[y].Index) : x.CompareTo(y));

        lock (_lock)
        {
            if (_disposed) return Undecided(eligible, 0, "the replayer is disposed");

            int next = 0;
            try
            {
                IReadOnlyTxProcessorSource source = _source ??= envFactory.Create();
                if (!source.TryBuildAtTarget(block.Header, out IReadOnlyTxProcessingScope? scope))
                    return Undecided(eligible, 0, "the parent state is unavailable");

                using (scope)
                {
                    IWorldState state = scope.WorldState;
                    ITransactionProcessor processor = scope.TransactionProcessor;
                    processor.SetBlockExecutionContext(block.Header);
                    AccessListCursor cursor = new(blockAccessList);
                    for (; next < order.Length; next++)
                    {
                        (Transaction transaction, int index) = requests[order[next]];
                        cursor.ApplyBefore(state, (uint)index + 1, spec);
                        eligible[order[next]] = Replay(state, processor, block, transaction, spec);
                    }
                }
            }
            catch (Exception e) when (e is IInternalNethermindException or StateUnavailableException or ObjectDisposedException or IOException)
            {
                // A fault this node is answerable for leaves every verdict not yet reached undecided.
                for (int i = next; i < order.Length; i++) eligible[order[i]] = true;
                if (_logger.IsDebug) _logger.Debug($"EIP-8369 replay undecided, enforcing the omissions: {e.Message}.");
            }
        }
        return eligible;
    }

    /// <summary>Runs one candidate's checks against <paramref name="state"/>, which it leaves as found.</summary>
    private bool Replay(IWorldState state, ITransactionProcessor processor, Block block, Transaction transaction, IReleaseSpec spec)
    {
        if (transaction.SenderAddress is not { } sender) return false;
        if (!IsNonceValid(state, transaction, sender)) return false;
        if (transaction.RecentRootReferences is not null && !spec.IsEip8272Enabled) return false;

        Address payer = FrameTxValidation.GetPrefixPaymaster(transaction) ?? sender;
        FrameTxValidationTracer tracer = new(sender, Eip8141Constants.ExpiryVerifierAddress, state, spec,
            payer: payer, storageSlotBound: Eip8369Constants.AaVopsSlotCount,
            maxVerifyGas: blocksConfig.FocilProfile2MaxVerifyGas is 0 ? ulong.MaxValue : blocksConfig.FocilProfile2MaxVerifyGas,
            recentRootAnchorSlot: block.Header.SlotNumber);
        try
        {
            // Prefix-only simulation restores its own snapshot, so the reconstructed state serves the next candidate.
            TransactionResult result = processor.Process(transaction, tracer, ExecutionOptions.FrameValidationPrefixOnly);
            return !tracer.Violated && result && tracer.Payer is not null;
        }
        catch (OperationCanceledException) when (tracer.Violated)
        {
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException
            and not IInternalNethermindException and not StateUnavailableException and not ObjectDisposedException and not IOException)
        {
            // The prefix is the expected source of a throw, as in FrameTxPrefixSimulator.
            if (_logger.IsDebug) _logger.Debug($"EIP-8369 replay of {transaction.Hash} threw; judging it ineligible. {e}");
            return false;
        }
    }

    /// <summary>Writes an access list's changes into a state in block-access-index order, each exactly once.</summary>
    /// <remarks>Anything the list omits is unchanged by the block, so the parent state already holds it, and
    /// advancing from one evaluation index to the next applies only the changes between them.</remarks>
    private sealed class AccessListCursor
    {
        private List<(uint Index, int Kind, int Item, int Change, ReadOnlyAccountChanges Account)> _changes = [];
        private int _next;

        private const int Balance = 0, Nonce = 1, Code = 2, Storage = 3;

        public AccessListCursor(ReadOnlyBlockAccessList blockAccessList)
        {
            foreach (ReadOnlyAccountChanges account in blockAccessList.AccountChanges.AsSpan())
            {
                for (int i = 0; i < account.BalanceChanges.Length; i++) _changes.Add((account.BalanceChanges[i].Index, Balance, i, 0, account));
                for (int i = 0; i < account.NonceChanges.Length; i++) _changes.Add((account.NonceChanges[i].Index, Nonce, i, 0, account));
                for (int i = 0; i < account.CodeChanges.Length; i++) _changes.Add((account.CodeChanges[i].Index, Code, i, 0, account));
                for (int slot = 0; slot < account.StorageChanges.Length; slot++)
                {
                    StorageChange[] changes = account.StorageChanges[slot].Changes;
                    for (int i = 0; i < changes.Length; i++) _changes.Add((changes[i].Index, Storage, slot, i, account));
                }
            }
            // OrderBy is stable, so a field changed twice at one index keeps the list's own order.
            _changes = [.. _changes.OrderBy(static c => c.Index)];
        }

        /// <summary>Applies every change recorded before <paramref name="blockAccessIndex"/> not yet applied.</summary>
        public void ApplyBefore(IWorldState state, uint blockAccessIndex, IReleaseSpec spec)
        {
            for (; _next < _changes.Count && _changes[_next].Index < blockAccessIndex; _next++)
            {
                (_, int kind, int item, int change, ReadOnlyAccountChanges account) = _changes[_next];
                Address address = account.Address;
                state.CreateAccountIfNotExists(address, UInt256.Zero);
                switch (kind)
                {
                    case Balance:
                        UInt256 target = account.BalanceChanges[item].Value;
                        UInt256 current = state.GetBalance(address);
                        if (target > current) state.AddToBalance(address, target - current, spec, out _);
                        else if (target < current) state.SubtractFromBalance(address, current - target, spec, out _);
                        break;
                    case Nonce:
                        state.SetNonce(address, account.NonceChanges[item].Value);
                        break;
                    case Code:
                        CodeChange code = account.CodeChanges[item];
                        state.InsertCode(address, code.CodeHash, code.Code ?? [], spec);
                        break;
                    default:
                        ReadOnlySlotChanges slot = account.StorageChanges[item];
                        state.Set(new StorageCell(address, slot.Key), slot.Changes[change].Value);
                        break;
                }
            }
        }
    }

    /// <summary>The EIP-8141 account nonce or EIP-8250 keyed nonce check, which prefix simulation leaves to its caller.</summary>
    private static bool IsNonceValid(IWorldState state, Transaction transaction, Address sender) =>
        transaction.NonceKeys is { } nonceKeys
            ? KeyedNonceManager.IsNonceSetValid(state, sender, nonceKeys, transaction.Nonce)
            : state.GetNonce(sender) == transaction.Nonce;

    private bool[] Undecided(bool[] eligible, int from, string reason)
    {
        if (_logger.IsDebug) _logger.Debug($"EIP-8369 replay undecided, enforcing the omissions: {reason}.");
        Array.Fill(eligible, true, from, eligible.Length - from);
        return eligible;
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
