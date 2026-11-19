// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapProvider : ISnapProvider
    {
        private readonly IDb _codeDb;
        private readonly ILogger _logger;
        private readonly ILogManager _logManager;

        private readonly ProgressTracker _progressTracker;
        private readonly ISnapTrieFactory _trieFactory;
        private readonly ConditionalWeakTable<PathWithAccount, AccountEvidence> _accountEvidence = [];
        internal sealed record AccountEvidence(AccountStruct? Account, ValueHash256 StateRoot);
        internal sealed record StorageEvidence(ValueHash256 Root, ValueHash256? Start, ValueHash256? Limit,
            ValueHash256[] Paths, AccountEvidence?[] Accounts, AccountStruct[] Fallback)
        {
            internal bool IsUnknown
            {
                get
                {
                    foreach (AccountEvidence? account in Accounts)
                        if (account is null || account.StateRoot != Root) return true;
                    return false;
                }
            }
        }

        internal StorageEvidence CaptureStorageEvidence(StorageRange request)
        {
            AccountEvidence?[] accounts = new AccountEvidence?[request.Accounts.Count];
            AccountStruct[] fallback = new AccountStruct[accounts.Length];
            ValueHash256[] paths = new ValueHash256[accounts.Length];
            for (int i = 0; i < accounts.Length; i++)
            {
                PathWithAccount account = request.Accounts[i];
                _accountEvidence.TryGetValue(account, out accounts[i]);
                fallback[i] = account.Account;
                paths[i] = account.Path;
            }
            return new(request.RootHash, request.StartingHash, request.LimitHash, paths, accounts, fallback);
        }

        internal bool IsAnchored(PathWithAccount account, ValueHash256 root) =>
            _accountEvidence.TryGetValue(account, out AccountEvidence evidence) && evidence.StateRoot == root;

        internal bool IsUnknown(StorageRange request)
        {
            foreach (PathWithAccount account in request.Accounts)
                if (!IsAnchored(account, request.RootHash)) return true;
            return false;
        }

        public SnapProvider(ProgressTracker progressTracker, [KeyFilter(DbNames.Code)] IDb codeDb, ISnapTrieFactory trieFactory, ILogManager logManager)
        {
            _progressTracker = progressTracker;
            _codeDb = codeDb;
            _trieFactory = trieFactory;
            _logManager = logManager;
            _logger = logManager.GetClassLogger<SnapProvider>();
            progressTracker.SetAccountEvidence(GetAccountEvidence, SetAccountEvidence);
        }

        private (AccountStruct? Account, ValueHash256 Root)? GetAccountEvidence(PathWithAccount account) =>
            _accountEvidence.TryGetValue(account, out AccountEvidence evidence) ? (evidence.Account, evidence.StateRoot) : null;

        private void SetAccountEvidence(PathWithAccount account, AccountStruct? value, ValueHash256 root) =>
            _accountEvidence.AddOrUpdate(account, new(value, root));

        internal Hash256? CurrentRoot => _progressTracker.CurrentRoot;
        internal bool HasPendingRootWork => _progressTracker.HasPendingRootWork;
        internal SnapRecoveryCoordinator Recovery => _progressTracker.Recovery;

        // This is actually close to 97% effective.
        private readonly AssociativeKeyCache<ValueHash256> _codeExistKeyCache = new(1024 * 16);

        // How many consecutive empty storage-range responses for one account are treated as "this account really has
        // no storage at the pivot" rather than "this peer is behind". Per account, not per queued range: the partitions
        // of a split account share the counter, because the question the promotion answers is one about the account.
        // Scaled off SnapSyncFeed.AllowedInvalidResponses, but it is a heuristic about the account, not a proof that a
        // fresh pivot has been seen - RefreshAccounts retries a still-stale root rather than dropping the account.
        internal const int MaxConsecutiveEmptyStorageResponses = 2 * (SnapSyncFeed.AllowedInvalidResponses + 1);

        // One empty response bumps the counter on every account of the request, and a storage batch holds up to
        // ProgressTracker.STORAGE_BATCH_SIZE (1200) of them, so an unguarded promotion could hand over a whole batch at
        // once. Past this many the account goes back to the storage queue with its streak intact, to be promoted later.
        internal const int MaxQueuedEmptyStreakRefreshes = 64;

        public bool CanSync() => _progressTracker.CanSync();

        public bool IsFinished(out SnapSyncBatch? nextBatch)
        {
            bool finished = _progressTracker.IsFinished(out nextBatch);
            if (nextBatch is not null)
            {
                nextBatch.RecoveryContext = Recovery;
                nextBatch.ProviderContext = this;
            }
            return finished;
        }

        public AddRangeResult AddAccountRange(AccountRange request, AccountsAndProofs response)
        {
            AddRangeResult result;

            if (response.PathAndAccounts.Count == 0)
            {
                _logger.Trace($"SNAP - GetAccountRange - requested expired RootHash:{request.RootHash}");

                result = AddRangeResult.ExpiredRootHash;
            }
            else
            {
                result = AddAccountRange(
                    request.BlockNumber.Value,
                    request.RootHash,
                    request.StartingHash,
                    response.PathAndAccounts,
                    response.Proofs,
                    hashLimit: request.LimitHash);

                if (result == AddRangeResult.OK)
                {
                    Interlocked.Add(ref Metrics.SnapSyncedAccounts, response.PathAndAccounts.Count);
                }
            }

            Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: false, result: result));
            return result;
        }

        public AddRangeResult AddAccountRange(
            ulong blockNumber,
            in ValueHash256 expectedRootHash,
            in ValueHash256 startingHash,
            IReadOnlyList<PathWithAccount> accounts,
            IByteArrayList proofs = null,
            in ValueHash256? hashLimit = null!)
        {
            if (accounts.Count == 0)
                throw new ArgumentException("Cannot be empty.", nameof(accounts));
            ValueHash256 effectiveHashLimit = hashLimit ?? ValueKeccak.MaxValue;

            (AddRangeResult result, bool moreChildrenToRight, List<PathWithAccount> accountsWithStorage, List<ValueHash256> codeHashes, Hash256 actualRootHash) =
                SnapProviderHelper.AddAccountRange(_trieFactory, blockNumber, expectedRootHash, startingHash, effectiveHashLimit, accounts, proofs,
                    _progressTracker.GetAccountPartitionStart(effectiveHashLimit));

            if (result == AddRangeResult.OK)
            {
                foreach (PathWithAccount item in accountsWithStorage) SetAccountEvidence(item, item.Account, expectedRootHash);
                _progressTracker.EnqueueAccountStorages(CollectionsMarshal.AsSpan(accountsWithStorage));

                foreach (ValueHash256 code in CollectionsMarshal.AsSpan(codeHashes))
                {
                    if (_codeExistKeyCache.Get(code)) continue;

                    if (_codeDb.KeyExists(code.Bytes))
                    {
                        _codeExistKeyCache.Set(code);
                        continue;
                    }

                    _progressTracker.EnqueueCodeHash(code);
                }

                ValueHash256 nextPath = accounts[^1].Path.IncrementPath();
                _progressTracker.UpdateAccountRangePartitionProgress(effectiveHashLimit, nextPath, moreChildrenToRight);
            }
            if (_logger.IsTrace)
            {
                string message = result switch
                {
                    AddRangeResult.MissingRootHashInProofs => $"SNAP - AddAccountRange failed, missing root hash {actualRootHash} in the proofs, startingHash:{startingHash}",
                    AddRangeResult.DifferentRootHash => $"SNAP - AddAccountRange failed, expected {blockNumber}:{expectedRootHash} but was {actualRootHash}, startingHash:{startingHash}",
                    AddRangeResult.InvalidProofNode => $"SNAP - AddAccountRange failed, invalid proof node, startingHash:{startingHash}",
                    AddRangeResult.InvalidOrder => $"SNAP - AddAccountRange failed, accounts are not in sorted order, startingHash:{startingHash}",
                    AddRangeResult.OutOfBounds => $"SNAP - AddAccountRange failed, accounts are out of bounds, startingHash:{startingHash}",
                    AddRangeResult.EmptyRange => $"SNAP - AddAccountRange failed, empty accounts, startingHash:{startingHash}",
                    AddRangeResult.InvalidProof => $"SNAP - AddAccountRange failed, invalid proof, startingHash:{startingHash}",
                    _ => null
                };
                if (message is not null)
                {
                    _logger.Trace(message);
                }
            }

            return result;
        }

        public AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response)
            => AddStorageRangeWithResult(request, response, out _);

        public AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response, out bool madeProgress)
        {
            AddRangeResult result = AddStorageRangeWithResult(request, response, out long committedBytes);
            madeProgress = committedBytes > 0 || result == AddRangeResult.OK;
            return result;
        }

        internal AddRangeResult AddStorageRangeWithResult(StorageRange request, SlotsAndProofs response, out long committedBytes) =>
            AddStorageRangeWithResult(request, response, out committedBytes, out _, out _);

        internal AddRangeResult AddStorageRangeWithResult(StorageRange request, SlotsAndProofs response,
            out long committedBytes, out bool neutral, out bool rootedBytes, StorageEvidence? dispatchEvidence = null,
            SnapRecoveryCoordinator.Lease? lease = null)
        {
            committedBytes = 0;
            neutral = false;
            rootedBytes = false;
            StorageEvidence captured = dispatchEvidence ?? CaptureStorageEvidence(request);
            AccountEvidence?[] evidence = captured.Accounts;
            if (captured.Root != request.RootHash || captured.Start != request.StartingHash || captured.Limit != request.LimitHash ||
                captured.Paths.Length != request.Accounts.Count) return AddRangeResult.InvalidProof;
            for (int i = 0; i < captured.Paths.Length; i++)
                if (captured.Paths[i] != request.Accounts[i].Path) return AddRangeResult.InvalidProof;
            ProgressTracker.ResponseAdmission admission = new(_progressTracker.Recovery, lease, _progressTracker.GetPreparedCohort(request));
            ReadOnlySpan<IOwnedReadOnlyList<PathWithStorageSlot>> responses = response.PathsAndSlots.AsSpan();
            // An empty storage batch has no account a proof could be for, so any reply to it is out of bounds.
            if (request.Accounts.Count == 0) return AddRangeResult.OutOfBounds;
            if (responses.Length > request.Accounts.Count)
            {
                _progressTracker.RequeueStorageRange(request.Copy());
                return AddRangeResult.OutOfBounds;
            }
            if (responses.Length == 0)
            {
                AccountEvidence? first = evidence[0];
                if (response.Proofs.Count == 0)
                {
                    RequeueAfterEmptyResponse(request, admission, evidence);
                    neutral = true;
                    bool unresolved = false;
                    foreach (AccountEvidence? item in evidence)
                    {
                        if (IsProvenEmpty(item, request)) continue;
                        unresolved = true;
                        if (!IsStale(item, request)) neutral = false;
                    }
                    if (!unresolved) neutral = false;
                    return unresolved ? AddRangeResult.ExpiredRootHash : AddRangeResult.OK;
                }
                using StorageRange empty = request.Copy();
                PathWithAccount account = new(captured.Paths[0], first is null ? captured.Fallback[0] : first.Account ?? AccountStruct.TotallyEmpty);
                empty.Accounts.Dispose();
                empty.Accounts = new ArrayPoolList<PathWithAccount>(1) { account };
                AddRangeResult emptyResult = SnapProviderHelper.VerifyEmptyStorageRange(empty, response.Proofs);
                if (emptyResult == AddRangeResult.OK)
                {
                    _progressTracker.OnCompletedLargeStorage(request.Accounts[0]);
                    for (int i = 1; i < request.Accounts.Count; i++) RequeueStorageAccount(request, i);
                }
                else
                {
                    neutral = emptyResult == AddRangeResult.MissingRootHashInProofs && IsStale(first, request);
                    if (neutral) RecoverStorageAccount(request, 0, admission);
                    else RequeueStorageAccount(request, 0);
                    for (int i = 1; i < request.Accounts.Count; i++) RequeueStorageAccount(request, i);
                }
                return emptyResult;
            }

            AddRangeResult result = AddRangeResult.OK;
            AddRangeResult driftResult = AddRangeResult.OK;
            bool sawDrift = false;
            int accountIndex = 0;
            int slotCount = 0;
            bool positional = responses.Length == request.Accounts.Count;
            HashSet<ValueHash256> staleAssociations = [];
            for (int responseIndex = 0; responseIndex < responses.Length; responseIndex++, accountIndex++)
            {
                if (accountIndex >= request.Accounts.Count)
                {
                    result = AddRangeResult.OutOfBounds;
                    break;
                }
                IReadOnlyList<PathWithStorageSlot> slots = responses[responseIndex];
                IByteArrayList? proofs = responseIndex == responses.Length - 1 ? response.Proofs : null;
                AddRangeResult accountResult;
                try
                {
                    AccountEvidence? authority = evidence[accountIndex];
                    accountResult = AddStorageRangeForAccount(request, accountIndex, slots, proofs, authority, out Hash256 actualRoot, captured.Fallback[accountIndex]);
                    bool mismatch = accountResult is AddRangeResult.DifferentRootHash or AddRangeResult.MissingRootHashInProofs;
                    if (!positional && mismatch && IsStale(authority, request))
                    {
                        int candidate = -1;
                        int lastCandidate = request.Accounts.Count - (responses.Length - responseIndex);
                        for (int next = accountIndex + 1; next <= lastCandidate; next++)
                        {
                            if (evidence[next]?.Account is not { } accepted) continue;
                            bool matches = accepted.StorageRoot == actualRoot;
                            if (!matches && accountResult == AddRangeResult.MissingRootHashInProofs && proofs is not null)
                                for (int i = 0; i < proofs.Count; i++)
                                    if (ValueKeccak.Compute(proofs[i]) == accepted.StorageRoot) { matches = true; break; }
                            if (!matches) continue;
                            if (candidate != -1) { candidate = -1; break; }
                            candidate = next;
                        }
                        if (candidate != -1)
                        {
                            AddRangeResult associated = AddStorageRangeForAccount(request, candidate, slots, proofs, evidence[candidate], out _, captured.Fallback[candidate]);
                            if (associated == AddRangeResult.OK)
                            {
                                for (; accountIndex < candidate; accountIndex++) RequeueOmittedStorageAccount(request, accountIndex, admission);
                                accountResult = associated;
                                authority = evidence[accountIndex];
                                sawDrift = true;
                            }
                            else if (Severity(associated) > Severity(result)) result = associated;
                        }
                    }
                    if (!positional && accountResult is AddRangeResult.DifferentRootHash or AddRangeResult.MissingRootHashInProofs &&
                        !IsStale(authority, request) && authority?.Account is { } current && staleAssociations.Contains(current.StorageRoot))
                    {
                        if (proofs is { Count: > 0 })
                        {
                            using MemDb proofDb = new();
                            ISnapTrieFactory isolated = new PatriciaSnapTrieFactory(new NodeStorage(proofDb), NullDb.Instance, _logManager);
                            AddRangeResult coherent = SnapProviderHelper.VerifyAlternateStorageRange(isolated, captured.Paths[accountIndex], slots,
                                accountIndex == 0 ? captured.Start : null, accountIndex == 0 ? captured.Limit : null, proofs);
                            if (coherent != AddRangeResult.OK && Severity(coherent) > Severity(result)) result = coherent;
                        }
                        for (int uncovered = accountIndex; uncovered < request.Accounts.Count; uncovered++) RequeueStorageAccount(request, uncovered);
                        sawDrift = true;
                        for (int tail = responseIndex + 1; tail < responses.Length; tail++)
                        {
                            int tailAccount = accountIndex + tail - responseIndex;
                            if (tailAccount >= request.Accounts.Count)
                            {
                                result = AddRangeResult.OutOfBounds;
                                break;
                            }
                            PathWithAccount identity = new(captured.Paths[tailAccount],
                                evidence[tailAccount]?.Account ?? captured.Fallback[tailAccount]);
                            AddRangeResult tailResult = SnapProviderHelper.VerifyStorageRange(_trieFactory, identity,
                                responses[tail], null, null, tail == responses.Length - 1 ? response.Proofs : null);
                            bool alternate = tailResult is AddRangeResult.DifferentRootHash or AddRangeResult.MissingRootHashInProofs &&
                                tail == responses.Length - 1 && response.Proofs is { Count: > 0 };
                            if (alternate)
                            {
                                using MemDb proofDb = new();
                                ISnapTrieFactory isolated = new PatriciaSnapTrieFactory(new NodeStorage(proofDb), NullDb.Instance, _logManager);
                                tailResult = SnapProviderHelper.VerifyAlternateStorageRange(isolated, identity.Path, responses[tail], null, null,
                                    tail == responses.Length - 1 ? response.Proofs : null);
                            }
                            if ((alternate || tailResult is not (AddRangeResult.OK or AddRangeResult.DifferentRootHash)) &&
                                Severity(tailResult) > Severity(result)) result = tailResult;
                        }
                        accountIndex = request.Accounts.Count;
                        break;
                    }
                    if (!positional && slots.Count > 0 && IsStale(authority, request) && authority?.Account is { } associatedRoot)
                        staleAssociations.Add(associatedRoot.StorageRoot);
                    bool drift = IsStale(authority, request) && accountResult is AddRangeResult.DifferentRootHash or AddRangeResult.MissingRootHashInProofs;
                    if (drift && (accountResult == AddRangeResult.MissingRootHashInProofs || proofs is { Count: > 0 }))
                    {
                        using MemDb proofDb = new();
                        ISnapTrieFactory isolated = new PatriciaSnapTrieFactory(new NodeStorage(proofDb), NullDb.Instance, _logManager);
                        AddRangeResult coherent = SnapProviderHelper.VerifyAlternateStorageRange(isolated, captured.Paths[accountIndex], slots,
                            accountIndex == 0 ? captured.Start : null, accountIndex == 0 ? captured.Limit : null, proofs);
                        if (coherent != AddRangeResult.OK) { accountResult = coherent; drift = false; }
                    }
                    if (drift)
                    {
                        sawDrift = true;
                        if (Severity(accountResult) > Severity(driftResult)) driftResult = accountResult;
                    }
                    else if (Severity(accountResult) > Severity(result)) result = accountResult;
                }
                catch
                {
                    if (accountIndex > 0)
                    {
                        IOwnedReadOnlyList<PathWithAccount> originalAccounts = request.Accounts;
                        request.Accounts = originalAccounts.AsSpan()[accountIndex..].ToPooledList();
                        request.StartingHash = null;
                        request.LimitHash = null;
                        originalAccounts.Dispose();
                        _progressTracker.ReportStorageRequestFinished(accountIndex);
                    }
                    throw;
                }
                Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: true, result: accountResult));
                if (accountResult == AddRangeResult.OK)
                {
                    slotCount += slots.Count;
                    foreach (PathWithStorageSlot slot in slots) committedBytes += 32 + slot.SlotRlpValue.Length;
                    rootedBytes |= evidence[accountIndex]?.StateRoot == request.RootHash;
                }
                else RecoverStorageAccount(request, accountIndex, admission);
            }
            for (; accountIndex < request.Accounts.Count; accountIndex++) RequeueStorageAccount(request, accountIndex);
            if (slotCount > 0) Interlocked.Add(ref Metrics.SnapSyncedStorageSlots, slotCount);
            neutral = sawDrift && result == AddRangeResult.OK;
            return neutral && driftResult != AddRangeResult.OK ? driftResult : result;
        }

        private void RecoverStorageAccount(StorageRange request, int index, ProgressTracker.ResponseAdmission admission)
        {
            if (!_progressTracker.TryEnqueueAccountRefresh(request.Accounts[index], index == 0 ? request.StartingHash : null,
                index == 0 ? request.LimitHash : null, admission)) RequeueStorageAccount(request, index);
        }

        private static bool IsStale(AccountEvidence? evidence, StorageRange request) =>
            evidence is null || evidence.StateRoot != request.RootHash;

        private static bool IsProvenEmpty(AccountEvidence? evidence, StorageRange request) =>
            evidence is not null && evidence.StateRoot == request.RootHash && evidence.Account is not { HasStorage: true };

        private void RequeueOmittedStorageAccount(StorageRange request, int index, ProgressTracker.ResponseAdmission admission)
        {
            PathWithAccount account = request.Accounts[index];
            if (Interlocked.Increment(ref account.EmptyStorageResponses) >= MaxConsecutiveEmptyStorageResponses &&
                TryRefreshAfterEmptyStreak(account, index == 0 ? request.StartingHash : null,
                    index == 0 ? request.LimitHash : null, request.BlockNumber, false, admission))
                Interlocked.Exchange(ref account.EmptyStorageResponses, 0);
            else RequeueStorageAccount(request, index);
        }

        private static int Severity(AddRangeResult result) => result switch
        {
            AddRangeResult.OK => 0,
            AddRangeResult.ExpiredRootHash => 1,
            AddRangeResult.EmptyRange => 2,
            AddRangeResult.MissingRootHashInProofs => 3,
            AddRangeResult.DifferentRootHash => 4,
            AddRangeResult.InvalidOrder => 5,
            AddRangeResult.OutOfBounds => 6,
            AddRangeResult.InvalidProofNode => 7,
            AddRangeResult.InvalidProof => 8,
            _ => ThrowInvalidRangeResult(result)
        };

        [DoesNotReturn, StackTraceHidden]
        private static int ThrowInvalidRangeResult(AddRangeResult result) =>
            throw new ArgumentOutOfRangeException(nameof(result), result, null);

        /// <summary>
        /// An empty storage-range response (no slots, no proof) is what a peer sends when it no longer has the requested
        /// state root - but it is also what geth sends for an account that has no storage at that root. Re-queueing
        /// cannot tell the two apart, and a pivot move does not help an account whose storage is gone, so after a streak
        /// the account is re-proven at the current pivot: the refresh resumes the range from the same starting hash if
        /// the storage still exists and drops it if it does not.
        /// </summary>
        private void RequeueAfterEmptyResponse(StorageRange request, ProgressTracker.ResponseAdmission admission, AccountEvidence?[] evidence)
        {
            ReadOnlySpan<PathWithAccount> accounts = request.Accounts.AsSpan();
            if (accounts.Length == 1)
            {
                PathWithAccount account = accounts[0];
                if (IsProvenEmpty(evidence[0], request))
                {
                    _progressTracker.OnCompletedLargeStorage(account);
                    return;
                }
                // Only a large-storage continuation carries a non-zero origin, and only it is unreachable by any other
                // request, so a stall there stalls the account for good - the lane #13155 was reproduced on, and the
                // only one worth an operator warning. A one-account batch is an ordinary account and stays on Debug.
                bool isLargeStorageContinuation = request.StartingHash is { } origin && origin > ValueKeccak.Zero;
                if (Interlocked.Increment(ref account.EmptyStorageResponses) < MaxConsecutiveEmptyStorageResponses
                    || !TryRefreshAfterEmptyStreak(account, request.StartingHash, request.LimitHash, request.BlockNumber, warn: isLargeStorageContinuation, admission))
                {
                    _progressTracker.RequeueStorageRange(request.Copy());
                }
                else
                {
                    // Without this the account is past the threshold for good and is promoted once per empty response
                    // instead of once per streak. Not inside TryRefreshAfterEmptyStreak, whose queue-full early return
                    // has to keep the streak.
                    Interlocked.Exchange(ref account.EmptyStorageResponses, 0);
                }

                return;
            }

            for (int i = 0; i < accounts.Length; i++)
            {
                PathWithAccount account = accounts[i];
                if (IsProvenEmpty(evidence[i], request))
                {
                    _progressTracker.OnCompletedLargeStorage(account);
                    continue;
                }
                ValueHash256? start = i == 0 ? request.StartingHash : null;
                ValueHash256? limit = i == 0 ? request.LimitHash : null;
                if (Interlocked.Increment(ref account.EmptyStorageResponses) < MaxConsecutiveEmptyStorageResponses
                    || !TryRefreshAfterEmptyStreak(account, start, limit, request.BlockNumber, warn: false, admission))
                {
                    RequeueStorageAccount(request, i);
                }
                else
                {
                    Interlocked.Exchange(ref account.EmptyStorageResponses, 0);
                }
            }
        }

        private void RequeueStorageAccounts(StorageRange request)
        {
            for (int i = 0; i < request.Accounts.Count; i++) RequeueStorageAccount(request, i);
        }

        private void RequeueStorageAccount(StorageRange request, int index)
        {
            if (index == 0 && (request.StartingHash > ValueKeccak.Zero || request.LimitHash < ValueKeccak.MaxValue))
            {
                _progressTracker.EnqueueNextSlot(new StorageRange
                {
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { request.Accounts[index] },
                    BlockNumber = request.BlockNumber,
                    RootHash = request.RootHash,
                    StartingHash = request.StartingHash,
                    LimitHash = request.LimitHash,
                    PrevRunRoot = request.PrevRunRoot,
                    PrevRunStart = request.PrevRunStart
                });
            }
            else _progressTracker.EnqueueAccountStorage(request.Accounts[index]);
        }

        /// <summary>
        /// Hands an account whose storage range keeps coming back empty to an account refresh, unless the refresh
        /// queue is already at <see cref="MaxQueuedEmptyStreakRefreshes"/>.
        /// </summary>
        /// <returns><c>false</c> when the caller must re-queue the range itself instead.</returns>
        private bool TryRefreshAfterEmptyStreak(PathWithAccount account, in ValueHash256? startingHash, in ValueHash256? limitHash, ulong? blockNumber, bool warn, ProgressTracker.ResponseAdmission admission)
        {
            if (!_progressTracker.TryEnqueueAccountRefresh(account, startingHash, limitHash, admission))
            {
                if (_logger.IsDebug) _logger.Debug($"Snap - {MaxQueuedEmptyStreakRefreshes} accounts are already queued for re-verification; re-queueing the storage range of {account.Path} instead.");
                return false;
            }

            string message = $"Snap - storage range of account {account.Path} came back empty {account.EmptyStorageResponses} times in a row (start: {startingHash ?? ValueKeccak.Zero}, pivot: {blockNumber}). Re-verifying the account at the current pivot instead of retrying.";
            if (warn)
            {
                if (_logger.IsWarn) _logger.Warn(message);
            }
            else if (_logger.IsDebug)
            {
                _logger.Debug(message);
            }

            Interlocked.Increment(ref Metrics.SnapStorageRangesRefreshedAfterEmptyResponses);
            return true;
        }

        public AddRangeResult AddStorageRangeForAccount(StorageRange request, int accountIndex, IReadOnlyList<PathWithStorageSlot> slots, IByteArrayList? proofs = null)
        {
            AddRangeResult result = AddStorageRangeForAccount(request, accountIndex, slots, proofs,
                _accountEvidence.TryGetValue(request.Accounts[accountIndex], out AccountEvidence evidence) ? evidence : null, out _);
            if (result != AddRangeResult.OK)
                _progressTracker.EnqueueAccountRefresh(request.Accounts[accountIndex], accountIndex == 0 ? request.StartingHash : null,
                    accountIndex == 0 ? request.LimitHash : null);
            return result;
        }

        private AddRangeResult AddStorageRangeForAccount(StorageRange request, int accountIndex,
            IReadOnlyList<PathWithStorageSlot> slots, IByteArrayList? proofs, AccountEvidence? evidence, out Hash256 actualRootHash, AccountStruct? fallback = null)
        {
            ReadOnlySpan<PathWithAccount> accounts = request.Accounts.AsSpan();
            PathWithAccount pathWithAccount = accounts[accountIndex];
            PathWithAccount capturedAccount = new(pathWithAccount.Path, evidence is null ? fallback ?? pathWithAccount.Account : evidence.Account ?? AccountStruct.TotallyEmpty);
            ValueHash256? startingHash = accountIndex == 0 ? request.StartingHash : null;
            ValueHash256? limitHash = accountIndex == 0 ? request.LimitHash : null;
            try
            {
                // A refresh of the other half of a split storage can replace the account meanwhile, and the range has
                // to be verified against the root its run is decided on.
                ValueHash256 storageRoot = capturedAccount.Account.StorageRoot;
                ValueHash256 runStart = accountIndex == 0 && request.PrevRunRoot == storageRoot ? request.PrevRunStart : startingHash ?? ValueKeccak.Zero;

                (AddRangeResult result, bool moreChildrenToRight, Hash256 computedRoot, bool isRootPersisted) = SnapProviderHelper.AddStorageRange(_trieFactory, capturedAccount, slots, startingHash, runStart, limitHash, proofs);
                actualRootHash = computedRoot;
                if (result == AddRangeResult.OK)
                {
                    // Tentative association must preserve skipped accounts' history; only verified storage ends the streak.
                    Interlocked.Exchange(ref pathWithAccount.EmptyStorageResponses, 0);
                    if (moreChildrenToRight)
                    {
                        _progressTracker.EnqueueNextSlot(request, accountIndex, slots[^1].Path, slots.Count, storageRoot, runStart);
                    }
                    else if (accountIndex == 0 && request.Accounts.Count == 1)
                    {
                        _progressTracker.OnCompletedLargeStorage(pathWithAccount);
                    }

                    if (!moreChildrenToRight && (limitHash == null || limitHash == ValueKeccak.MaxValue) && !isRootPersisted)
                    {
                        // The root is persisted only once every range of the storage is stitched under it, which takes
                        // them all to be downloaded at one root without a split, so a refreshed or split storage is left
                        // for healing.
                        _progressTracker.TrackAccountToHeal(accounts[accountIndex].Path);
                    }

                    return result;
                }

                if (_logger.IsTrace)
                {
                    string message = result switch
                    {
                        AddRangeResult.MissingRootHashInProofs => $"SNAP - AddStorageRange failed, missing root hash {actualRootHash} in the proofs, startingHash:{request.StartingHash}",
                        AddRangeResult.DifferentRootHash => $"SNAP - AddStorageRange failed, expected storage root hash:{capturedAccount.Account.StorageRoot} but was {actualRootHash}, startingHash:{request.StartingHash}",
                        AddRangeResult.InvalidProofNode => $"SNAP - AddStorageRange failed, invalid proof node, startingHash:{request.StartingHash}",
                        AddRangeResult.InvalidOrder => $"SNAP - AddStorageRange failed, slots are not in sorted order, startingHash:{request.StartingHash}",
                        AddRangeResult.OutOfBounds => $"SNAP - AddStorageRange failed, slots are out of bounds, startingHash:{request.StartingHash}",
                        AddRangeResult.EmptyRange => $"SNAP - AddStorageRange failed, slots list is empty, startingHash:{request.StartingHash}",
                        AddRangeResult.InvalidProof => $"SNAP - AddStorageRange failed, invalid proof, startingHash:{request.StartingHash}",
                        _ => null
                    };
                    if (message is not null)
                    {
                        _logger.Trace(message);
                    }
                }

                return result;

            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"Error in storage {e}");
                throw;
            }
        }

        public AddRangeResult RefreshAccounts(AccountsToRefreshRequest request, AccountsAndProofs response)
        {
            AccountWithStorageStartingHash requestedPath = request.Paths[0];
            ValueHash256 path = requestedPath.PathAndAccount.Path;

            AddRangeResult result;
            switch (VerifyRefreshedAccount(response, request.RootHash, path, out AccountStruct account))
            {
                case RefreshVerifyResult.Verified:
                    result = AddRangeResult.OK;
                    _progressTracker.RecordRefreshedAccount(requestedPath, account, request.RootHash);
                    // Read and reset in one step, for the same reason as above.
                    int emptyResponses = Interlocked.Exchange(ref requestedPath.PathAndAccount.EmptyStorageResponses, 0);

                    if (!account.HasStorage)
                    {
                        // The storage was emptied after the account was discovered. There is nothing left to fetch, and
                        // asking for it would only draw more empty responses; the account stays tracked for healing.
                        if (_logger.IsInfo) _logger.Info($"Snap - account {path} has no storage at the current pivot anymore (empty responses: {emptyResponses}, start: {requestedPath.StorageStartingHash}), dropping its storage range.");
                        _progressTracker.DropLargeStorageProgress(requestedPath.PathAndAccount);
                        break;
                    }

                    if (requestedPath.StorageStartingHash == ValueKeccak.Zero && requestedPath.StorageHashLimit == ValueKeccak.MaxValue)
                        _progressTracker.DropLargeStorageProgress(requestedPath.PathAndAccount);
                    break;

                case RefreshVerifyResult.NotFound:
                    // The account no longer exists at the pivot, so there is no storage to retrieve. It remains
                    // tracked for healing. Terminal success - must not retry or the refresh would loop forever.
                    result = AddRangeResult.OK;
                    _progressTracker.RecordRefreshedAccount(requestedPath, null, request.RootHash);
                    // Terminal like the !HasStorage branch above, so it owes the same bookkeeping.
                    _progressTracker.DropLargeStorageProgress(requestedPath.PathAndAccount);
                    break;

                case RefreshVerifyResult.Expired:
                    // The peer does not have the state for this root (stale pivot).
                    result = AddRangeResult.ExpiredRootHash;
                    RetryAccountRefresh(requestedPath);
                    break;

                default: // InvalidProof - the proof does not reconstruct the state root.
                    result = AddRangeResult.DifferentRootHash;
                    RetryAccountRefresh(requestedPath);
                    break;
            }

            Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: false, result: result));
            return result;
        }

        private enum RefreshVerifyResult { Verified, NotFound, Expired, InvalidProof }

        /// <summary>
        /// Reconstructs the returned account range and verifies it against <paramref name="stateRoot"/> in an
        /// isolated, empty-backed trie - the account leaf is set from the response, not assumed to be in the proof -
        /// then extracts the verified account at <paramref name="path"/>. Nothing is written to the client state DB.
        /// </summary>
        private RefreshVerifyResult VerifyRefreshedAccount(AccountsAndProofs response, Hash256 stateRoot, in ValueHash256 path, out AccountStruct account)
        {
            account = default;
            IReadOnlyList<PathWithAccount> accounts = response.PathAndAccounts;
            // An empty response carries no range to verify, so the account's absence cannot be proven here -
            // retry rather than concluding (unproven) that it was deleted.
            if (accounts.Count == 0)
                return RefreshVerifyResult.Expired;

            AddRangeResult result;
            try
            {
                // Empty-backed isolated factory: a proof node that cannot be resolved from the proof itself fails
                // verification instead of being completed from (or racing) the live client state DB.
                ISnapTrieFactory factory = new PatriciaSnapTrieFactory(new NodeStorage(new MemDb()), NullDb.Instance, _logManager);
                result = SnapProviderHelper.VerifyAccountRange(factory, stateRoot, path, path.IncrementPath(), accounts, response.Proofs);
            }
            catch (Exception)
            {
                // The proof is untrusted P2P data: any failure assembling or decoding it (trie, RLP, bounds, etc.)
                // means the proof is invalid, never a crash of the sync task.
                return RefreshVerifyResult.InvalidProof;
            }

            if (result != AddRangeResult.OK)
                return RefreshVerifyResult.InvalidProof;

            // The verified range proves which accounts exist around [path, path + 1]; filter to the requested one.
            // Its absence here is therefore a proven non-existence (deleted account), not an unverified guess.
            for (int i = 0; i < accounts.Count; i++)
            {
                if (accounts[i].Path == path)
                {
                    account = accounts[i].Account;
                    return RefreshVerifyResult.Verified;
                }
            }
            return RefreshVerifyResult.NotFound;
        }

        private void RetryAccountRefresh(AccountWithStorageStartingHash requestedPath) => _progressTracker.RetryAccountRefresh(requestedPath, storage: false);

        public void AddCodes(IReadOnlyList<ValueHash256> requestedHashes, IByteArrayList codes)
            => AddCodesWithResult(requestedHashes, codes);

        internal (int Matched, long Bytes) AddCodesWithResult(IReadOnlyList<ValueHash256> requestedHashes, IByteArrayList codes)
            => AddCodesWithResult(requestedHashes, codes, out _);

        internal (int Matched, long Bytes) AddCodesWithResult(IReadOnlyList<ValueHash256> requestedHashes, IByteArrayList codes, out bool invalidResponse)
        {
            HashSet<ValueHash256> requested = [.. requestedHashes];
            HashSet<ValueHash256> set = [.. requestedHashes];
            int matched = 0;
            long bytes = 0;
            invalidResponse = false;

            using (IWriteBatch writeBatch = _codeDb.StartWriteBatch())
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    ReadOnlySpan<byte> codeSpan;
                    try
                    {
                        codeSpan = codes[i];
                    }
                    catch (Nethermind.Serialization.FluentRlp.RlpReaderException)
                    {
                        invalidResponse = true;
                        break;
                    }
                    ValueHash256 codeHash = ValueKeccak.Compute(codeSpan);

                    if (!requested.Contains(codeHash)) invalidResponse = true;
                    else if (set.Remove(codeHash))
                    {
                        matched++;
                        bytes += codeSpan.Length;
                        byte[] code = codeSpan.ToArray();
                        Interlocked.Add(ref Metrics.SnapStateSynced, code.Length);
                        writeBatch[codeHash.Bytes] = code;
                    }
                }
            }

            Interlocked.Add(ref Metrics.SnapSyncedCodes, matched);

            foreach (ValueHash256 unserved in set)
            {
                _progressTracker.EnqueueCodeHash(unserved);
            }
            return (matched, bytes);
        }

        public IDisposable? BeginResponse() => _progressTracker.BeginResponse();

        public void ReleaseRequest(SnapSyncBatch batch, bool responseHandled, bool madeRangeProgress = false)
        {
            _progressTracker.ReportRequestReleased(batch, madeRangeProgress);
            if (batch.AccountRangeRequest is not null)
            {
                // Re-offered from the progress it recorded, so the flag makes no difference here.
                _progressTracker.ReportAccountRangePartitionFinished(batch.AccountRangeRequest.LimitHash.Value);
            }
            else if (batch.StorageRangeRequest is not null)
            {
                if (!_progressTracker.CompleteStorageRefreshRetry(batch.StorageRangeRequest, responseHandled) && !responseHandled)
                {
                    _progressTracker.RequeueStorageRange(batch.StorageRangeRequest.Copy());
                }

                _progressTracker.ReportStorageRequestFinished(batch.StorageRangeRequest.Accounts.Count);
                _progressTracker.ReleasePreparedCohort(batch.StorageRangeRequest);
            }
            else if (batch.CodesRequest is not null)
            {
                _progressTracker.ReportCodeRequestFinished(responseHandled ? [] : batch.CodesRequest.AsSpan());
            }
            else if (batch.AccountsToRefreshRequest is not null)
            {
                _progressTracker.ReportAccountRefreshFinished(batch.AccountsToRefreshRequest, responseHandled);
            }
        }

        public bool IsSnapGetRangesFinished() => _progressTracker.IsSnapGetRangesFinished();

        public void UpdatePivot() => _progressTracker.UpdatePivot();

        public void Dispose() => _codeExistKeyCache.Clear();

    }
}
