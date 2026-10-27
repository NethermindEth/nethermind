// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool.Collections;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Nethermind.TxPool
{
    public partial class TxPool
    {
        public IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender() =>
            _blobTransactions.GetBucketSnapshot();

        public IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender(bool filterToReadyTx, UInt256 baseFee = default) =>
            DropUnreadySenders(_blobTransactions.GetBucketSnapshot(), filterToReadyTx, baseFee);

        public Transaction[] GetPendingLightBlobTransactionsBySender(Address address) =>
            _blobTransactions.GetBucketSnapshot(address);

        public int GetPendingBlobTransactionsCount() => _blobTransactions.Count;

        public bool TryGetBlobAndProofV0(byte[] blobVersionedHash,
            [NotNullWhen(true)] out byte[]? blob,
            [NotNullWhen(true)] out byte[]? proof)
        {
            if (_blobTransactions.TryGetBlobAndProofV0(blobVersionedHash, out blob, out proof))
            {
                return true;
            }

            if (!TryGetIncludedBlob(blobVersionedHash, ProofVersion.V0, out ShardBlobNetworkWrapper? wrapper, out int blobIndex))
            {
                return false;
            }

            blob = wrapper.Blobs[blobIndex];
            proof = wrapper.Proofs[blobIndex];
            return true;
        }

        public bool TryGetBlobAndProofV1(byte[] blobVersionedHash,
            [NotNullWhen(true)] out byte[]? blob,
            [NotNullWhen(true)] out byte[][]? cellProofs)
            => _blobTransactions.TryGetBlobAndProofV1(blobVersionedHash, out blob, out cellProofs);

        public int TryGetBlobsAndProofsV1(byte[][] requestedBlobVersionedHashes,
            Span<byte[]?> blobs, Span<ReadOnlyMemory<byte[]>> proofs)
        {
            int found = _blobTransactions.TryGetBlobsAndProofsV1(requestedBlobVersionedHashes, blobs, proofs);
            return found == requestedBlobVersionedHashes.Length || _includedBlobLocations is null
                ? found
                : found + FillIncludedBlobsAndProofsV1(requestedBlobVersionedHashes, blobs, proofs);
        }

        public bool TryGetPendingBlobCellMask(in ValueHash256 hash, out BlobCellMask availableMask)
            => _blobTransactions.TryGetAvailableCellMask(hash, out availableMask);

        public bool TryGetPendingBlobCellMetadata(
            Hash256 hash,
            out BlobCellMask availableMask,
            out int blobCount,
            out int materializationWork) =>
            _blobTransactions.TryGetAvailableCellMetadata(
                hash,
                out availableMask,
                out blobCount,
                out materializationWork);

        public bool TryGetBlobCells(Hash256 hash, BlobCellMask requestedMask, out BlobCellMask availableMask, [NotNullWhen(true)] out byte[][]? cells)
            => _blobTransactions.TryGetCells(hash, requestedMask, out availableMask, out cells);

        public bool TryGetBlobCellsAndProofsV1(byte[] blobVersionedHash, BlobCellMask requestedMask, out BlobCellMask availableMask, [NotNullWhen(true)] out byte[][]? cells, [NotNullWhen(true)] out byte[][]? proofs)
            => _blobTransactions.TryGetBlobCellsAndProofsV1(blobVersionedHash, requestedMask, out availableMask, out cells, out proofs)
               || TryGetIncludedBlobCellsAndProofs(blobVersionedHash, requestedMask, out availableMask, out cells, out proofs);

        /// <inheritdoc/>
        /// <remarks>Blobs left unserved by the blob pool are grouped by processed row, so a row holding several requested blobs is read and decoded once.</remarks>
        public int TryGetBlobCellsAndProofsV1(byte[][] requestedBlobVersionedHashes, BlobCellMask requestedMask,
            BlobCellMask[] availableMasks, byte[][]?[] cells, byte[][]?[] proofs)
        {
            int found = 0;
            for (int i = 0; i < requestedBlobVersionedHashes.Length; i++)
            {
                if (_blobTransactions.TryGetBlobCellsAndProofsV1(requestedBlobVersionedHashes[i], requestedMask, out availableMasks[i], out cells[i], out proofs[i]))
                {
                    found++;
                }
            }

            return found == requestedBlobVersionedHashes.Length || _includedBlobLocations is null
                ? found
                : found + FillIncludedBlobCellsAndProofsV1(requestedBlobVersionedHashes, requestedMask, availableMasks, cells, proofs);
        }

        public bool TryMergeBlobCells(Hash256 hash, BlobCellMask cellMask, byte[][] cells) =>
            MergeBlobCells(hash, cellMask, cells) == BlobCellMergeResult.Accepted;

        public BlobCellMergeResult MergeBlobCells(Hash256 hash, BlobCellMask cellMask, byte[][] cells)
        {
            BlobCellMergeResult result = _blobTransactions.MergeCells(hash, cellMask, cells);
            if (result != BlobCellMergeResult.Accepted)
            {
                return result;
            }

            if (_blobTransactions.TryGetValue(hash, out Transaction? transaction))
            {
                _broadcaster.Broadcast(transaction, isPersistent: false);
            }

            return BlobCellMergeResult.Accepted;
        }

        public bool TryGetPendingBlobTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? blobTransaction) =>
            _blobTransactions.TryGetValue(hash, out blobTransaction);

        public AcceptTxResult ValidateTxForBlobSampling(Transaction tx)
        {
            // Type-3, or on eth/73 a blob-carrying frame transaction whose covering wrapper validated (EIP-8437).
            if (!tx.SupportsBlobs && !(tx.SupportsFrames && tx.CarriesBlobs))
            {
                return AcceptTxResult.Invalid;
            }

            TxFilteringState state = default;
            _newHeadLock.EnterReadLock();
            try
            {
                state = new(tx, _accounts, _specProvider.GetCurrentHeadSpec());
                bool canRecycle = false;
                return FilterTransactions(tx, TxHandlingOptions.None, ref state, ref canRecycle, skipSamplingDeferredFilters: true);
            }
            finally
            {
                state.ProofReservation?.Dispose();
                state.SenderProofReservation?.Dispose();
                state.SenderAdmissionGate?.Exit();
                _newHeadLock.ExitReadLock();
            }
        }

        // only for tests - to test sorting
        internal void TryGetBlobTxSortingEquivalent(Hash256 hash, out Transaction? transaction)
            => _blobTransactions.TryGetBlobTxSortingEquivalent(hash, out transaction);

        public void ForgetRejectedBlobTransaction(Hash256 hash) => _hashCache.DeleteFromCurrentBlock(hash);

        public bool ForgetInvalidBlobProofs(Hash256 hash) => _hashCache.TryDeleteInvalidBlobProofs(hash);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RecordUnrecoverableReorgedBlobTx(Transaction blobTx, Block previousBlock)
        {
            Metrics.PendingTransactionsUnresolvableSender++;
            if (_logger.IsDebug) _logger.Debug($"Skipped readding tx {blobTx.Hash} from reorged block {previousBlock.Number} (hash {previousBlock.Hash}) to blob pool: sender address is not recoverable");
        }

        /// <summary>
        /// How many blocks an included blob stays servable. It is above the normal finality lag, so finality is the usual
        /// bound and this only limits growth of the index when finality stalls.
        /// </summary>
        internal const ulong IncludedBlobMaxBlockDistance = 128;

        /// <summary>The locations of recently included blobs, or <see langword="null"/> unless the pool saves processed blob transactions to a storage that serves single rows.</summary>
        internal IncludedBlobLocations? IncludedBlobLocations => _includedBlobLocations;

        /// <summary>Gets the blob with versioned hash <paramref name="blobVersionedHash"/> from the processed row of a recently included transaction.</summary>
        /// <returns><see langword="true"/> if the indexed row holds that blob in full with <paramref name="requiredVersion"/> proofs.</returns>
        private bool TryGetIncludedBlob(byte[] blobVersionedHash, ProofVersion requiredVersion,
            [NotNullWhen(true)] out ShardBlobNetworkWrapper? wrapper, out int blobIndex)
        {
            wrapper = null;
            blobIndex = -1;
            if (_includedBlobLocations is null
                || !_includedBlobLocations.TryGetLocation(blobVersionedHash, out IncludedBlobLocation location)
                || !_processedBlobTxStorage!.TryGetProcessedTransaction(location.BlockNumber, location.Position, out Transaction? row))
            {
                return false;
            }

            blobIndex = location.BlobIndex;
            return TryGetServableIncludedBlob(row, blobVersionedHash, blobIndex, requiredVersion, out wrapper);
        }

        /// <summary>Fills the entries the blob pool left empty from the processed rows of recently included transactions.</summary>
        /// <returns>The number of entries filled.</returns>
        /// <remarks>Requests are grouped by row, so a row holding several requested blobs is read and decoded once.</remarks>
        private int FillIncludedBlobsAndProofsV1(byte[][] requestedBlobVersionedHashes,
            Span<byte[]?> blobs, Span<ReadOnlyMemory<byte[]>> proofs)
        {
            using ArrayPoolListRef<IncludedBlobRead> reads = new(requestedBlobVersionedHashes.Length, requestedBlobVersionedHashes.Length);
            using ArrayPoolListRef<Transaction?> rows = ReadIncludedBlobRows(requestedBlobVersionedHashes, blobs, reads.AsSpan(), out int readCount);

            int filled = 0;
            foreach (IncludedBlobRead read in reads.AsSpan()[..readCount])
            {
                if (TryGetServableIncludedBlob(rows[read.RowIndex], requestedBlobVersionedHashes[read.OutputIndex], read.BlobIndex,
                        ProofVersion.V1, out ShardBlobNetworkWrapper? wrapper))
                {
                    blobs[read.OutputIndex] = wrapper.Blobs[read.BlobIndex];
                    proofs[read.OutputIndex] = new ReadOnlyMemory<byte[]>(wrapper.Proofs, Ckzg.CellsPerExtBlob * read.BlobIndex, Ckzg.CellsPerExtBlob);
                    filled++;
                }
            }

            return filled;
        }

        /// <summary>Reads, each once, the processed rows that hold the requested blobs left unserved by the blob pool.</summary>
        /// <param name="requestedBlobVersionedHashes">The requested versioned hashes.</param>
        /// <param name="served">The response entries per requested hash; an entry the blob pool filled is skipped.</param>
        /// <param name="reads">Receives one read per indexed blob, pointing into the returned rows.</param>
        /// <param name="readCount">The number of reads written to <paramref name="reads"/>.</param>
        /// <returns>The rows, <see langword="null"/> where a row is missing.</returns>
        private ArrayPoolListRef<Transaction?> ReadIncludedBlobRows<T>(byte[][] requestedBlobVersionedHashes, Span<T?> served,
            Span<IncludedBlobRead> reads, out int readCount) where T : class
        {
            using ArrayPoolListRef<ProcessedBlobTxKey> rowKeys = new(requestedBlobVersionedHashes.Length);
            readCount = 0;
            for (int i = 0; i < requestedBlobVersionedHashes.Length; i++)
            {
                if (served[i] is not null
                    || !_includedBlobLocations!.TryGetLocation(requestedBlobVersionedHashes[i], out IncludedBlobLocation location))
                {
                    continue;
                }

                ProcessedBlobTxKey rowKey = new(location.BlockNumber, location.Position);
                int rowIndex = rowKeys.AsSpan().IndexOf(rowKey);
                if (rowIndex < 0)
                {
                    rowIndex = rowKeys.Count;
                    rowKeys.Add(rowKey);
                }

                reads[readCount++] = new IncludedBlobRead(i, rowIndex, location.BlobIndex);
            }

            ArrayPoolListRef<Transaction?> rows = new(rowKeys.Count, rowKeys.Count);
            if (rowKeys.Count > 0)
            {
                try
                {
                    _processedBlobTxStorage!.TryGetProcessedTransactions(rowKeys.AsSpan(), rows.AsSpan());
                }
                catch
                {
                    rows.Dispose();
                    throw;
                }
            }

            return rows;
        }

        /// <summary>Checks that a processed row read back for an indexed location still holds the requested blob in a servable form.</summary>
        /// <remarks>
        /// The rows change independently of the index, by a reorg rewriting a height or by finalization cleanup, so a row
        /// is served only when it holds the requested versioned hash at the indexed blob index, with full blobs and proofs
        /// of <paramref name="requiredVersion"/>, as the blob pool requires of its own transactions.
        /// </remarks>
        private static bool TryGetServableIncludedBlob(Transaction? row, byte[] blobVersionedHash, int blobIndex,
            ProofVersion requiredVersion, [NotNullWhen(true)] out ShardBlobNetworkWrapper? wrapper)
        {
            int proofsPerBlob = requiredVersion == ProofVersion.V1 ? Ckzg.CellsPerExtBlob : 1;
            if (row?.BlobVersionedHashes is { } rowBlobVersionedHashes
                && (uint)blobIndex < (uint)rowBlobVersionedHashes.Length
                && Bytes.AreEqual(rowBlobVersionedHashes[blobIndex], blobVersionedHash)
                && row.NetworkWrapper is ShardBlobNetworkWrapper rowWrapper
                && rowWrapper.Version == requiredVersion
                && rowWrapper.HasFullBlobs()
                && blobIndex < rowWrapper.Blobs.Length
                && rowWrapper.Proofs.Length / proofsPerBlob > blobIndex)
            {
                Interlocked.Increment(ref Metrics.IncludedBlobsServed);
                wrapper = rowWrapper;
                return true;
            }

            wrapper = null;
            return false;
        }

        private readonly record struct IncludedBlobRead(int OutputIndex, int RowIndex, int BlobIndex);

        /// <summary>Fills the entries the blob pool left empty from the processed rows of recently included transactions.</summary>
        /// <returns>The number of entries filled.</returns>
        private int FillIncludedBlobCellsAndProofsV1(byte[][] requestedBlobVersionedHashes, BlobCellMask requestedMask,
            Span<BlobCellMask> availableMasks, Span<byte[][]?> cells, Span<byte[][]?> proofs)
        {
            using ArrayPoolListRef<IncludedBlobRead> reads = new(requestedBlobVersionedHashes.Length, requestedBlobVersionedHashes.Length);
            using ArrayPoolListRef<Transaction?> rows = ReadIncludedBlobRows(requestedBlobVersionedHashes, cells, reads.AsSpan(), out int readCount);

            int filled = 0;
            foreach (IncludedBlobRead read in reads.AsSpan()[..readCount])
            {
                int i = read.OutputIndex;
                if (TryGetServableIncludedBlob(rows[read.RowIndex], requestedBlobVersionedHashes[i], read.BlobIndex,
                        ProofVersion.V1, out ShardBlobNetworkWrapper? wrapper)
                    && TryBuildIncludedBlobCellsAndProofs(wrapper, read.BlobIndex, requestedMask, out availableMasks[i], out cells[i], out proofs[i]))
                {
                    filled++;
                }
            }

            return filled;
        }

        /// <summary>Gets the requested cells and their proofs of a blob from the processed row of a recently included transaction.</summary>
        private bool TryGetIncludedBlobCellsAndProofs(byte[] blobVersionedHash, BlobCellMask requestedMask, out BlobCellMask availableMask,
            [NotNullWhen(true)] out byte[][]? cells, [NotNullWhen(true)] out byte[][]? proofs)
        {
            if (TryGetIncludedBlob(blobVersionedHash, ProofVersion.V1, out ShardBlobNetworkWrapper? wrapper, out int blobIndex))
            {
                return TryBuildIncludedBlobCellsAndProofs(wrapper, blobIndex, requestedMask, out availableMask, out cells, out proofs);
            }

            availableMask = default;
            cells = null;
            proofs = null;
            return false;
        }

        /// <summary>Builds the requested cells and their proofs of the blob at <paramref name="blobIndex"/> of an included transaction's wrapper.</summary>
        /// <remarks>The row holds the full blob, so the cells are computed from that blob alone, as the blob pool does for its full-blob transactions.</remarks>
        private static bool TryBuildIncludedBlobCellsAndProofs(ShardBlobNetworkWrapper wrapper, int blobIndex, BlobCellMask requestedMask,
            out BlobCellMask availableMask, [NotNullWhen(true)] out byte[][]? cells, [NotNullWhen(true)] out byte[][]? proofs)
        {
            if (!BlobTxDistinctSortedPool.TryBuildBlobCellsAndProofsResponse(
                    [new BlobTxDistinctSortedPool.BlobCellsCandidate(wrapper, blobIndex)], requestedMask, out availableMask, out cells, out proofs)
                // Never null on success; TryBuildBlobCellsAndProofsResponse does not annotate its outs, so these satisfy the [NotNullWhen] contract.
                || cells is null
                || proofs is null)
            {
                availableMask = default;
                cells = null;
                proofs = null;
                return false;
            }

            return true;
        }

        /// <summary>Marks the blob txs of a reorganising <paramref name="head"/>, so that the copies the re-adds read back from
        /// the processed rows are kept for <see cref="SaveProcessedBlobTransactions"/>.</summary>
        /// <remarks>When the new head includes a blob tx the replaced block also included, the re-add is rejected by nonce and
        /// the replaced block's rows are deleted, so the tx is in neither the pool nor the rows; without the kept copy, the new
        /// head's rows would miss it. Only the new head's blob txs are kept, so a deep rewind does not hold every rewound one.</remarks>
        private void CollectReorganisedBlobTxs(Block head, bool reorganised)
        {
            _reorganisedBlobTxs.Clear();
            if (!reorganised || !_blobReorgsSupportEnabled) return;

            foreach (Transaction tx in head.Transactions)
            {
                if (tx.CarriesBlobs)
                {
                    _reorganisedBlobTxs[tx.Hash!.ValueHash256] = null;
                }
            }
        }

        /// <summary>Saves the pool-held blob transactions of <paramref name="block"/> to the processed rows and indexes their blobs.</summary>
        /// <returns>The failure of the save, which the caller rethrows once the block's transactions have left the pool.</returns>
        /// <remarks>
        /// Does nothing unless the pool supports reorgs. Otherwise the index is updated for every block, also when nothing
        /// was saved or the save failed, so that a block re-processed at a height drops the locations recorded there before.
        /// The failure is deferred rather than handled: the head update stops at the same point as when the save followed
        /// the removal.
        /// </remarks>
        private ExceptionDispatchInfo? SaveProcessedBlobTransactions(Block block)
        {
            if (!_blobReorgsSupportEnabled)
            {
                return null;
            }

            Transaction[] blockTransactions = block.Transactions;
            using ArrayPoolListRef<Transaction> blobTxsToSave = new((int)_specProvider.GetSpec(block.Header).MaxBlobCount);
            try
            {
                for (int i = 0; i < blockTransactions.Length; i++)
                {
                    Transaction blockTx = blockTransactions[i];
                    if (!blockTx.CarriesBlobs)
                    {
                        continue;
                    }

                    if (_blobTransactions.TryGetValue(blockTx.Hash, out Transaction? fullBlobTx)
                        || (_reorganisedBlobTxs.TryGetValue(blockTx.Hash!.ValueHash256, out fullBlobTx) && fullBlobTx is not null))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Saved processed blob tx {blockTx.Hash} from block {block.Number} to ProcessedTxs db");
                        blobTxsToSave.Add(fullBlobTx);
                    }
                    else if (_logger.IsTrace) _logger.Trace($"Skipped adding processed blob tx {blockTx.Hash} from block {block.Number} to ProcessedTxs db - not found in blob pool");
                }

                if (blobTxsToSave.Count > 0)
                {
                    _blobTxStorage.AddBlobTransactionsFromBlock(block.Number, blobTxsToSave);
                }
            }
            catch (Exception e)
            {
                // The head loop logs the rethrown exception, so the context travels in its message instead of a second log line.
                _includedBlobLocations?.Record(block.Number, block.Hash, []);
                return ExceptionDispatchInfo.Capture(new InvalidOperationException(
                    $"Failed to save {blobTxsToSave.Count} processed blob txs of block {block.Number}; they cannot be re-added on a reorg.", e));
            }

            _includedBlobLocations?.Record(block.Number, block.Hash, blobTxsToSave.AsSpan());
            return null;
        }
    }
}
