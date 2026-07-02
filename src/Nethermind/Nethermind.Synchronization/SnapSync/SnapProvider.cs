// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
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

            _progressTracker.ReportAccountRangePartitionFinished(request.LimitHash.Value);
            response.Dispose();

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

                try
                {
                    using ArrayPoolListRef<ValueHash256> filteredCodeHashes = codeHashes.AsParallel().Where((code) =>
                    {
                        if (_codeExistKeyCache.Get(code)) return false;

                        bool exist = _codeDb.KeyExists(code.Bytes);
                        if (exist) _codeExistKeyCache.Set(code);
                        return !exist;
                    }).ToPooledListRef(codeHashes.Count);

                    _progressTracker.EnqueueCodeHashes(filteredCodeHashes.AsSpan());
                }
                catch (AggregateException ae) when (ae.Flatten().InnerExceptions is { Count: > 0 } inners
                    && inners.All(e => e is ObjectDisposedException))
                {
                    ExceptionDispatchInfo.Capture(inners[0]).Throw();
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

                _progressTracker.RetryStorageRange(request.Copy());
                Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: true, result: AddRangeResult.ExpiredRootHash));

                return AddRangeResult.ExpiredRootHash;
            }
            else
            {
                int slotCount = 0;

                int requestLength = request.Accounts.Count;

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

                if (requestLength > responses.Length)
                {
                    _progressTracker.ReportFullStorageRequestFinished(requestLength, request.Accounts.AsSpan()[responses.Length..]);
                }
                else
                {
                    _progressTracker.ReportFullStorageRequestFinished(requestLength);
                }

                if (result == AddRangeResult.OK && slotCount > 0)
                {
                    Interlocked.Add(ref Metrics.SnapSyncedStorageSlots, slotCount);
                }
            }

            response.Dispose();
            return result;
        }

        public AddRangeResult AddStorageRangeForAccount(StorageRange request, int accountIndex, IReadOnlyList<PathWithStorageSlot> slots, IByteArrayList? proofs = null)
        {
            ReadOnlySpan<PathWithAccount> accounts = request.Accounts.AsSpan();
            PathWithAccount pathWithAccount = accounts[accountIndex];

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
                        _progressTracker.EnqueueAccountStorage(requestedPath.PathAndAccount);
                    }
                    break;

                case RefreshVerifyResult.NotFound:
                    // The account no longer exists at the pivot, so there is no storage to retrieve. It remains
                    // tracked for healing. Terminal success - must not retry or the refresh would loop forever.
                    result = AddRangeResult.OK;
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
            // Must be the last statement, after any enqueue, so IsSnapGetRangesFinished cannot observe
            // an empty queue with a zeroed active count while work is still being scheduled.
            _progressTracker.ReportAccountRefreshFinished();
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
                ISnapTrieFactory factory = new PatriciaSnapTrieFactory(new NodeStorage(new MemDb()), logManager);
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
            codes.Dispose();
            _progressTracker.ReportCodeRequestFinished(set.ToArray());
        }

        public void RetryRequest(SnapSyncBatch batch)
        {
            if (batch.AccountRangeRequest is not null)
            {
                _progressTracker.ReportAccountRangePartitionFinished(batch.AccountRangeRequest.LimitHash.Value);
            }
            else if (batch.StorageRangeRequest is not null)
            {
                _progressTracker.RetryStorageRange(batch.StorageRangeRequest.Copy());
            }
            else if (batch.CodesRequest is not null)
            {
                _progressTracker.ReportCodeRequestFinished(batch.CodesRequest.AsSpan());
            }
            else if (batch.AccountsToRefreshRequest is not null)
            {
                _progressTracker.ReportAccountRefreshFinished(batch.AccountsToRefreshRequest);
            }
        }

        public bool IsSnapGetRangesFinished() => _progressTracker.IsSnapGetRangesFinished();

        public void UpdatePivot() => _progressTracker.UpdatePivot();

        public void Dispose() => _codeExistKeyCache.Clear();

    }
}
