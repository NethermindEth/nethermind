// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
    public class SnapProvider(ProgressTracker progressTracker, [KeyFilter(DbNames.Code)] IDb codeDb, ISnapTrieFactory trieFactory, ILogManager logManager) : ISnapProvider
    {
        private readonly IDb _codeDb = codeDb;
        private readonly ILogger _logger = logManager.GetClassLogger<SnapProvider>();

        private readonly ProgressTracker _progressTracker = progressTracker;
        private readonly ISnapTrieFactory _trieFactory = trieFactory;

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

        public bool IsFinished(out SnapSyncBatch? nextBatch) => _progressTracker.IsFinished(out nextBatch);

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
                SnapProviderHelper.AddAccountRange(_trieFactory, blockNumber, expectedRootHash, startingHash, effectiveHashLimit, accounts, proofs);

            if (result == AddRangeResult.OK)
            {
                foreach (PathWithAccount item in CollectionsMarshal.AsSpan(accountsWithStorage))
                {
                    _progressTracker.EnqueueAccountStorage(item);
                }

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
        {
            AddRangeResult result = AddRangeResult.OK;

            ReadOnlySpan<IOwnedReadOnlyList<PathWithStorageSlot>> responses = response.PathsAndSlots.AsSpan();
            if (responses.Length == 0 && response.Proofs.Count == 0)
            {
                _logger.Trace($"SNAP - GetStorageRange - expired BlockNumber:{request.BlockNumber}, RootHash:{request.RootHash}, (Accounts:{request.Accounts.Count}), {request.StartingHash}");

                RequeueAfterEmptyResponse(request);
                Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: true, result: AddRangeResult.ExpiredRootHash));

                return AddRangeResult.ExpiredRootHash;
            }

            if (responses.Length > request.Accounts.Count)
            {
                if (_logger.IsTrace) _logger.Trace($"SNAP - GetStorageRange - got {responses.Length} slot lists for {request.Accounts.Count} accounts, RootHash:{request.RootHash}");

                _progressTracker.RequeueStorageRange(request.Copy());
                Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: true, result: AddRangeResult.OutOfBounds));

                return AddRangeResult.OutOfBounds;
            }

            int slotCount = 0;
            for (int i = 0; i < responses.Length; i++)
            {
                // only the last can have proofs
                IByteArrayList proofs = null;
                if (i == responses.Length - 1)
                {
                    proofs = response.Proofs;
                }

                result = AddStorageRangeForAccount(request, i, responses[i], proofs);
                Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: true, result: result));

                slotCount += responses[i].Count;
            }

            if (result == AddRangeResult.OK && slotCount > 0)
            {
                Interlocked.Add(ref Metrics.SnapSyncedStorageSlots, slotCount);
            }

            foreach (PathWithAccount uncovered in request.Accounts.AsSpan()[responses.Length..])
            {
                _progressTracker.EnqueueAccountStorage(uncovered);
            }

            return result;
        }

        /// <summary>
        /// An empty storage-range response (no slots, no proof) is what a peer sends when it no longer has the requested
        /// state root - but it is also what geth sends for an account that has no storage at that root. Re-queueing
        /// cannot tell the two apart, and a pivot move does not help an account whose storage is gone, so after a streak
        /// the account is re-proven at the current pivot: the refresh resumes the range from the same starting hash if
        /// the storage still exists and drops it if it does not.
        /// </summary>
        private void RequeueAfterEmptyResponse(StorageRange request)
        {
            ReadOnlySpan<PathWithAccount> accounts = request.Accounts.AsSpan();
            if (accounts.Length == 1)
            {
                PathWithAccount account = accounts[0];
                // Only a large-storage continuation carries a non-zero origin, and only it is unreachable by any other
                // request, so a stall there stalls the account for good - the lane #13155 was reproduced on, and the
                // only one worth an operator warning. A one-account batch is an ordinary account and stays on Debug.
                bool isLargeStorageContinuation = request.StartingHash is { } origin && origin > ValueKeccak.Zero;
                if (Interlocked.Increment(ref account.EmptyStorageResponses) < MaxConsecutiveEmptyStorageResponses
                    || !TryRefreshAfterEmptyStreak(account, request.StartingHash, request.LimitHash, request.BlockNumber, warn: isLargeStorageContinuation))
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

            // A multi-account request is not a continuation: each account goes back to the batching queue on its own,
            // and a whole batch can cross the threshold on one response, so these stay on the Debug lane.
            foreach (PathWithAccount account in accounts)
            {
                if (Interlocked.Increment(ref account.EmptyStorageResponses) < MaxConsecutiveEmptyStorageResponses
                    || !TryRefreshAfterEmptyStreak(account, null, null, request.BlockNumber, warn: false))
                {
                    _progressTracker.EnqueueAccountStorage(account);
                }
                else
                {
                    Interlocked.Exchange(ref account.EmptyStorageResponses, 0);
                }
            }
        }

        /// <summary>
        /// Hands an account whose storage range keeps coming back empty to an account refresh, unless the refresh
        /// queue is already at <see cref="MaxQueuedEmptyStreakRefreshes"/>.
        /// </summary>
        /// <returns><c>false</c> when the caller must re-queue the range itself instead.</returns>
        private bool TryRefreshAfterEmptyStreak(PathWithAccount account, in ValueHash256? startingHash, in ValueHash256? limitHash, ulong? blockNumber, bool warn)
        {
            if (_progressTracker.AccountsToRefreshCount >= MaxQueuedEmptyStreakRefreshes)
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
            _progressTracker.EnqueueAccountRefresh(account, startingHash, limitHash);
            return true;
        }

        public AddRangeResult AddStorageRangeForAccount(StorageRange request, int accountIndex, IReadOnlyList<PathWithStorageSlot> slots, IByteArrayList? proofs = null)
        {
            ReadOnlySpan<PathWithAccount> accounts = request.Accounts.AsSpan();
            PathWithAccount pathWithAccount = accounts[accountIndex];
            // Peers are serving this account's storage again, so the empty streak is over. Interlocked because the
            // split path in EnqueueNextSlot queues both halves with the same PathWithAccount instance.
            Interlocked.Exchange(ref pathWithAccount.EmptyStorageResponses, 0);

            try
            {
                (AddRangeResult result, bool moreChildrenToRight, Hash256 actualRootHash, bool isRootPersisted) = SnapProviderHelper.AddStorageRange(_trieFactory, pathWithAccount, slots, request.StartingHash, request.LimitHash, proofs);
                if (result == AddRangeResult.OK)
                {
                    if (moreChildrenToRight)
                    {
                        _progressTracker.EnqueueNextSlot(request, accountIndex, slots[^1].Path, slots.Count);
                    }
                    else if (accountIndex == 0 && request.Accounts.Count == 1)
                    {
                        _progressTracker.OnCompletedLargeStorage(pathWithAccount);
                    }

                    if (!moreChildrenToRight && (request.LimitHash == null || request.LimitHash == ValueKeccak.MaxValue) && !isRootPersisted)
                    {
                        // Sometimes the stitching does not work. Likely because part of the storage is using different
                        // pivot, sometimes the proof is in a form that we cannot cleanly verify if it should persist or not,
                        // but also because of stitching bug. So we just force trigger healing and continue on with our lives.
                        _progressTracker.TrackAccountToHeal(accounts[accountIndex].Path);
                    }

                    return result;
                }

                if (_logger.IsTrace)
                {
                    string message = result switch
                    {
                        AddRangeResult.MissingRootHashInProofs => $"SNAP - AddStorageRange failed, missing root hash {actualRootHash} in the proofs, startingHash:{request.StartingHash}",
                        AddRangeResult.DifferentRootHash => $"SNAP - AddStorageRange failed, expected storage root hash:{pathWithAccount.Account.StorageRoot} but was {actualRootHash}, startingHash:{request.StartingHash}",
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

                _progressTracker.EnqueueAccountRefresh(pathWithAccount, request.StartingHash, request.LimitHash);
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
            switch (VerifyRefreshedAccount(response, request.RootHash, path, out Account? account))
            {
                case RefreshVerifyResult.Verified:
                    result = AddRangeResult.OK;
                    requestedPath.PathAndAccount.Account = requestedPath.PathAndAccount.Account.WithChangedStorageRoot(account!.StorageRoot);
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

                    if (requestedPath.StorageStartingHash > ValueKeccak.Zero)
                    {
                        _progressTracker.EnqueueNextSlot(new StorageRange
                        {
                            Accounts = new ArrayPoolList<PathWithAccount>(1) { requestedPath.PathAndAccount },
                            StartingHash = requestedPath.StorageStartingHash,
                            LimitHash = requestedPath.StorageHashLimit
                        });
                    }
                    else
                    {
                        // Back to the batching queue at origin 0, so whatever large-storage progress this account had
                        // is obsolete and nothing on that queue will ever retire it.
                        _progressTracker.DropLargeStorageProgress(requestedPath.PathAndAccount);
                        _progressTracker.EnqueueAccountStorage(requestedPath.PathAndAccount);
                    }
                    break;

                case RefreshVerifyResult.NotFound:
                    // The account no longer exists at the pivot, so there is no storage to retrieve. It remains
                    // tracked for healing. Terminal success - must not retry or the refresh would loop forever.
                    result = AddRangeResult.OK;
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
        private RefreshVerifyResult VerifyRefreshedAccount(AccountsAndProofs response, Hash256 stateRoot, in ValueHash256 path, out Account? account)
        {
            account = null;
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
                ISnapTrieFactory factory = new PatriciaSnapTrieFactory(new NodeStorage(new MemDb()), NullDb.Instance, logManager);
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

        private void RetryAccountRefresh(AccountWithStorageStartingHash requestedPath) => _progressTracker.EnqueueAccountRefresh(requestedPath.PathAndAccount, requestedPath.StorageStartingHash, requestedPath.StorageHashLimit);

        public void AddCodes(IReadOnlyList<ValueHash256> requestedHashes, IByteArrayList codes)
        {
            HashSet<ValueHash256> set = requestedHashes.ToHashSet();

            using (IWriteBatch writeBatch = _codeDb.StartWriteBatch())
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    ReadOnlySpan<byte> codeSpan = codes[i];
                    ValueHash256 codeHash = ValueKeccak.Compute(codeSpan);

                    if (set.Remove(codeHash))
                    {
                        byte[] code = codeSpan.ToArray();
                        Interlocked.Add(ref Metrics.SnapStateSynced, code.Length);
                        writeBatch[codeHash.Bytes] = code;
                    }
                }
            }

            Interlocked.Add(ref Metrics.SnapSyncedCodes, codes.Count);

            foreach (ValueHash256 unserved in set)
            {
                _progressTracker.EnqueueCodeHash(unserved);
            }
        }

        public void ReleaseRequest(SnapSyncBatch batch, bool responseHandled)
        {
            if (batch.AccountRangeRequest is not null)
            {
                // Re-offered from the progress it recorded, so the flag makes no difference here.
                _progressTracker.ReportAccountRangePartitionFinished(batch.AccountRangeRequest.LimitHash.Value);
            }
            else if (batch.StorageRangeRequest is not null)
            {
                if (!responseHandled)
                {
                    _progressTracker.RequeueStorageRange(batch.StorageRangeRequest.Copy());
                }

                _progressTracker.ReportStorageRequestFinished(batch.StorageRangeRequest.Accounts.Count);
            }
            else if (batch.CodesRequest is not null)
            {
                _progressTracker.ReportCodeRequestFinished(responseHandled ? [] : batch.CodesRequest.AsSpan());
            }
            else if (batch.AccountsToRefreshRequest is not null)
            {
                _progressTracker.ReportAccountRefreshFinished(responseHandled ? null : batch.AccountsToRefreshRequest);
            }
        }

        public bool IsSnapGetRangesFinished() => _progressTracker.IsSnapGetRangesFinished();

        public void UpdatePivot() => _progressTracker.UpdatePivot();

        public void Dispose() => _codeExistKeyCache.Clear();

    }
}
