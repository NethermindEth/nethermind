// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Paprika;
using Paprika.Data;
using Account = Nethermind.Core.Account;
using PaprikaAccount = Paprika.Account;
using PaprikaKeccak = Paprika.Crypto.Keccak;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Controls how Paprika persists the root page for a committed flat-state batch.
/// </summary>
public enum PaprikaFlatCommitMode
{
    /// <summary>
    /// Flushes data pages and the new root page on every commit.
    /// </summary>
    FlushDataAndRoot,

    /// <summary>
    /// Flushes data pages and writes the root page without an immediate root flush.
    /// The latest root becomes durable on a later flush/commit.
    /// </summary>
    FlushDataOnly,
}

public sealed class PaprikaFlatPersistence(
    IColumnsDb<FlatDbColumns> db,
    Paprika.IDb paprikaDb,
    PaprikaFlatCommitMode commitMode = PaprikaFlatCommitMode.FlushDataAndRoot) : IPersistence
{
    private static readonly byte[] PendingCurrentStateKey = Keccak.Compute("PaprikaPendingCurrentState").BytesToArray();
    private static readonly AccountDecoder AccountDecoder = AccountDecoder.Slim;

    private readonly PaprikaFlatCommitMode _commitMode = Enum.IsDefined(commitMode)
        ? commitMode
        : throw new ArgumentOutOfRangeException(nameof(commitMode), commitMode, null);
    private readonly WriteBufferAdjuster _adjuster = new(db);
    private readonly object _commitRecoveryLock = new();
    private int _layoutPersisted = BasePersistence.ValidateLayoutReturnFlag(db, FlatLayout.PaprikaFlat);

    public void Flush()
    {
        db.Flush();
        paprikaDb.Flush();
    }

    public void Clear() =>
        throw new NotSupportedException($"{nameof(FlatLayout.PaprikaFlat)} does not support in-place flat snap reset. Remove the Paprika DB directory before re-syncing.");

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        lock (_commitRecoveryLock)
        {
            RecoverPendingCurrentState();
            IColumnDbSnapshot<FlatDbColumns> snapshot = db.CreateSnapshot();
            IReadOnlyBatch? paprikaSnapshot = null;
            try
            {
                StateId currentState = BasePersistence.ReadCurrentState(snapshot.GetColumn(FlatDbColumns.Metadata));
                paprikaSnapshot = BeginPaprikaReadOnlyBatch(currentState);
                IReadOnlyBatch paprikaSnapshotForReader = paprikaSnapshot;

                BaseTriePersistence.Reader trieReader = new(
                    snapshot.GetColumn(FlatDbColumns.StateTopNodes),
                    snapshot.GetColumn(FlatDbColumns.StateNodes),
                    snapshot.GetColumn(FlatDbColumns.StorageNodes),
                    snapshot.GetColumn(FlatDbColumns.FallbackNodes)
                );

                return new BasePersistence.Reader<PaprikaFlatReader, BaseTriePersistence.Reader>(
                    new PaprikaFlatReader(paprikaSnapshotForReader),
                    trieReader,
                    currentState,
                    new Reactive.AnonymousDisposable(() =>
                    {
                        paprikaSnapshotForReader.Dispose();
                        snapshot.Dispose();
                    })
                );
            }
            catch
            {
                paprikaSnapshot?.Dispose();
                snapshot.Dispose();
                throw;
            }
        }
    }

    private IReadOnlyBatch BeginPaprikaReadOnlyBatch(in StateId currentState)
    {
        if (currentState.BlockNumber < 0)
        {
            IReadOnlyBatch latest = paprikaDb.BeginReadOnlyBatch();
            PaprikaKeccak expected = currentState.StateRoot.ToPaprikaKeccak();
            if (latest.Metadata.StateHash != PaprikaKeccak.Zero && latest.Metadata.StateHash != expected)
            {
                latest.Dispose();
                throw new InvalidOperationException($"{nameof(FlatLayout.PaprikaFlat)} state root is ahead of flat DB metadata. Remove the Paprika DB directory before restarting from this flat DB state.");
            }

            return latest;
        }

        PaprikaKeccak stateRoot = currentState.StateRoot.ToPaprikaKeccak();
        return paprikaDb.BeginReadOnlyBatch(in stateRoot);
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags)
    {
        lock (_commitRecoveryLock)
        {
            RecoverPendingCurrentState();
            IColumnDbSnapshot<FlatDbColumns> dbSnap = db.CreateSnapshot();
            StateId currentState = BasePersistence.ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
            if (from != StateId.Sync && to != StateId.Sync && currentState != from)
            {
                dbSnap.Dispose();
                throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
            }

            IColumnsWriteBatch<FlatDbColumns>? batch = null;
            IBatch? paprikaBatch = null;
            try
            {
                ValidatePaprikaLatestMatchesFrom(from);

                batch = db.StartWriteBatch();
                paprikaBatch = paprikaDb.BeginNextBatch();

                IWriteBatch stateTopNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StateTopNodes, flags);
                IWriteBatch stateNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StateNodes, flags);
                IWriteBatch storageNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.StorageNodes, flags);
                IWriteBatch fallbackNodesBatch = _adjuster.Wrap(batch, FlatDbColumns.FallbackNodes, flags);

                BaseTriePersistence.WriteBatch trieWriteBatch = new(
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateTopNodes),
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StateNodes),
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes),
                    (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.FallbackNodes),
                    stateTopNodesBatch,
                    stateNodesBatch,
                    storageNodesBatch,
                    fallbackNodesBatch,
                    flags);

                StateId fromCopy = from;
                StateId toCopy = to;
                IColumnsWriteBatch<FlatDbColumns> batchCopy = batch;
                IBatch paprikaBatchCopy = paprikaBatch;

                batch = null;
                paprikaBatch = null;

                return new BasePersistence.WriteBatch<PaprikaFlatWriteBatch, BaseTriePersistence.WriteBatch>(
                    new PaprikaFlatWriteBatch(paprikaBatchCopy),
                    trieWriteBatch,
                    new Reactive.AnonymousDisposable(() => CommitWriteBatch(batchCopy, paprikaBatchCopy, dbSnap, fromCopy, toCopy, flags))
                );
            }
            catch
            {
                paprikaBatch?.Dispose();
                batch?.Dispose();
                dbSnap.Dispose();
                _adjuster.OnBatchDisposed();
                throw;
            }
        }
    }

    private void ValidatePaprikaLatestMatchesFrom(in StateId from)
    {
        if (from == StateId.Sync)
        {
            return;
        }

        using IReadOnlyBatch latest = paprikaDb.BeginReadOnlyBatch();
        PaprikaKeccak expected = from.StateRoot.ToPaprikaKeccak();
        if (latest.Metadata.StateHash == PaprikaKeccak.Zero && from.BlockNumber < 0)
        {
            return;
        }

        if (latest.Metadata.StateHash != expected)
        {
            throw new InvalidOperationException($"{nameof(FlatLayout.PaprikaFlat)} latest state root {latest.Metadata.StateHash} does not match flat DB write base {from.StateRoot}.");
        }
    }

    private void CommitWriteBatch(IColumnsWriteBatch<FlatDbColumns> batch, IBatch paprikaBatch, IColumnDbSnapshot<FlatDbColumns> dbSnap, in StateId from, in StateId to, WriteFlags flags)
    {
        lock (_commitRecoveryLock)
        {
            bool batchDisposeAttempted = false;
            try
            {
                bool updatesCurrentState = from != StateId.Sync && to != StateId.Sync;
                IWriteBatch metadataBatch = batch.GetColumnBatch(FlatDbColumns.Metadata);
                BasePersistence.RecordLayoutOnFirstBatch(metadataBatch, ref _layoutPersisted, FlatLayout.PaprikaFlat);
                if (updatesCurrentState)
                {
                    SetPendingCurrentState(metadataBatch, to);
                }

                batchDisposeAttempted = true;
                batch.Dispose();

                if (updatesCurrentState && !flags.HasFlag(WriteFlags.DisableWAL))
                {
                    db.Flush(onlyWal: true);
                }

                if (updatesCurrentState)
                {
                    paprikaBatch.SetMetadata((uint)to.BlockNumber, to.StateRoot.ToPaprikaKeccak());
                }

                paprikaBatch.Commit(GetPaprikaCommitOptions(flags)).AsTask().GetAwaiter().GetResult();

                if (updatesCurrentState)
                {
                    using IColumnsWriteBatch<FlatDbColumns> currentStateBatch = db.StartWriteBatch();
                    IWriteBatch metadata = currentStateBatch.GetColumnBatch(FlatDbColumns.Metadata);
                    BasePersistence.SetCurrentState(metadata, to);
                    ClearPendingCurrentState(metadata);
                }
            }
            finally
            {
                if (!batchDisposeAttempted)
                {
                    batch.Dispose();
                }

                paprikaBatch.Dispose();
                dbSnap.Dispose();
                _adjuster.OnBatchDisposed();
            }

            if (!flags.HasFlag(WriteFlags.DisableWAL))
            {
                db.Flush(onlyWal: true);
            }
        }
    }

    private CommitOptions GetPaprikaCommitOptions(WriteFlags flags)
    {
        if (flags.HasFlag(WriteFlags.DisableWAL))
        {
            return CommitOptions.DangerNoFlush;
        }

        return _commitMode switch
        {
            PaprikaFlatCommitMode.FlushDataAndRoot => CommitOptions.FlushDataAndRoot,
            PaprikaFlatCommitMode.FlushDataOnly => CommitOptions.FlushDataOnly,
            _ => throw new InvalidOperationException($"Unsupported {nameof(PaprikaFlatCommitMode)} '{_commitMode}'."),
        };
    }

    private StateId RecoverPendingCurrentState()
    {
        IReadOnlyKeyValueStore metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        StateId currentState = BasePersistence.ReadCurrentState(metadata);
        StateId? maybePendingState = ReadPendingCurrentState(metadata);
        if (maybePendingState is null)
        {
            return currentState;
        }

        StateId pendingState = maybePendingState.Value;
        using IReadOnlyBatch latest = paprikaDb.BeginReadOnlyBatch();
        if (MatchesLatestPaprikaState(latest.Metadata.StateHash, pendingState))
        {
            using IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();
            IWriteBatch metadataBatch = batch.GetColumnBatch(FlatDbColumns.Metadata);
            BasePersistence.SetCurrentState(metadataBatch, pendingState);
            ClearPendingCurrentState(metadataBatch);
            return pendingState;
        }

        throw new InvalidOperationException(
            $"{nameof(FlatLayout.PaprikaFlat)} has pending flat DB metadata for {pendingState.StateRoot}, " +
            $"but Paprika latest state root is {latest.Metadata.StateHash} and flat DB current state root is {currentState.StateRoot}.");
    }

    private static bool MatchesLatestPaprikaState(PaprikaKeccak latest, in StateId state)
    {
        if (latest == state.StateRoot.ToPaprikaKeccak())
        {
            return true;
        }

        return state.BlockNumber < 0 && latest == PaprikaKeccak.Zero;
    }

    /// <summary>
    /// Reads the PaprikaFlat pending current-state marker from the metadata column.
    /// </summary>
    public static StateId? ReadPendingCurrentState(IReadOnlyKeyValueStore kv)
    {
        byte[]? bytes = kv.Get(PendingCurrentStateKey);
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        if (bytes.Length != 8 + 32)
        {
            throw new InvalidOperationException($"{nameof(FlatLayout.PaprikaFlat)} pending state metadata has invalid length {bytes.Length}.");
        }

        return new StateId(BinaryPrimitives.ReadInt64BigEndian(bytes), new ValueHash256(bytes[8..]));
    }

    /// <summary>
    /// Writes the PaprikaFlat pending current-state marker to the metadata column.
    /// </summary>
    public static void SetPendingCurrentState(IWriteOnlyKeyValueStore kv, in StateId stateId)
    {
        Span<byte> bytes = stackalloc byte[8 + 32];
        BinaryPrimitives.WriteInt64BigEndian(bytes[..8], stateId.BlockNumber);
        stateId.StateRoot.BytesAsSpan.CopyTo(bytes[8..]);
        kv.PutSpan(PendingCurrentStateKey, bytes);
    }

    private static void ClearPendingCurrentState(IWriteOnlyKeyValueStore kv) => kv.Remove(PendingCurrentStateKey);

    private static Account DecodeAccount(ReadOnlySpan<byte> payload)
    {
        PaprikaAccount.ReadFrom(payload, out PaprikaAccount account);
        return new Account(
            account.Nonce,
            account.Balance,
            new Hash256(account.StorageRootHash.Span),
            new Hash256(account.CodeHash.Span));
    }

    private static ReadOnlySpan<byte> EncodeAccount(Account account, Span<byte> buffer)
    {
        PaprikaKeccak codeHash = account.CodeHash.ValueHash256.ToPaprikaKeccak();
        PaprikaKeccak storageRoot = account.StorageRoot.ValueHash256.ToPaprikaKeccak();
        PaprikaAccount paprikaAccount = new(account.Balance, account.Nonce, codeHash, storageRoot);
        return paprikaAccount.WriteTo(buffer);
    }

    private static byte[] EncodeAccountToRlp(Account account)
    {
        using NettyRlpStream stream = AccountDecoder.EncodeToNewNettyStream(account);
        return stream.AsSpan().ToArray();
    }

    private readonly struct PaprikaFlatReader(IReadOnlyBatch batch) : BasePersistence.IFlatReader
    {
        public bool IsPreimageMode => false;

        public Account? GetAccount(Address address)
        {
            ValueHash256 accountPath = address.ToAccountPath;
            Key key = Key.Account(NibblePath.FromKey(accountPath.BytesAsSpan));
            if (!batch.TryGet(key, out ReadOnlySpan<byte> result) || result.IsEmpty)
            {
                return null;
            }

            return DecodeAccount(result);
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            ValueHash256 slotHash = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
            return TryGetSlotRaw(address.ToAccountPath, slotHash, ref outValue);
        }

        public byte[]? GetAccountRaw(in ValueHash256 addrHash)
        {
            Key key = Key.Account(NibblePath.FromKey(addrHash.BytesAsSpan));
            if (!batch.TryGet(key, out ReadOnlySpan<byte> result) || result.IsEmpty)
            {
                return null;
            }

            return EncodeAccountToRlp(DecodeAccount(result));
        }

        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slotHash, ref SlotValue outValue)
        {
            Key key = Key.StorageCell(NibblePath.FromKey(address.BytesAsSpan), slotHash.BytesAsSpan);
            if (!batch.TryGet(key, out ReadOnlySpan<byte> result) || result.IsEmpty)
            {
                return false;
            }

            outValue = SlotValue.FromSpanWithoutLeadingZero(result);
            return true;
        }

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
            throw new NotSupportedException($"{nameof(FlatLayout.PaprikaFlat)} does not support account iteration.");

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            throw new NotSupportedException($"{nameof(FlatLayout.PaprikaFlat)} does not support storage iteration.");
    }

    private struct PaprikaFlatWriteBatch(IBatch batch) : BasePersistence.IFlatWriteBatch
    {
        public void SelfDestruct(Address addr)
        {
            Key key = Key.StorageCell(NibblePath.FromKey(addr.ToAccountPath.Bytes), NibblePath.Empty);
            batch.DeleteByPrefix(key);
        }

        public void SetAccount(Address addr, Account? account)
        {
            if (account is null)
            {
                RemoveAccount(addr.ToAccountPath);
                return;
            }

            SetAccountRaw(addr.ToAccountPath, account);
        }

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
        {
            ValueHash256 slotHash = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
            SetStorageRaw(addr.ToAccountPath, slotHash, value);
        }

        private void RemoveAccount(in ValueHash256 address)
        {
            Key key = Key.Account(NibblePath.FromKey(address.BytesAsSpan));
            if (batch.TryGet(in key, out _))
            {
                batch.SetRaw(in key, ReadOnlySpan<byte>.Empty);
            }
        }

        public void SetAccountRaw(in ValueHash256 addrHash, Account account)
        {
            Span<byte> accountBuffer = stackalloc byte[PaprikaAccount.MaxByteCount];
            Key key = Key.Account(NibblePath.FromKey(addrHash.BytesAsSpan));
            batch.SetRaw(key, EncodeAccount(account, accountBuffer));
        }

        public void SetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, in SlotValue? value)
        {
            Key key = Key.StorageCell(NibblePath.FromKey(addrHash.BytesAsSpan), slotHash.BytesAsSpan);
            if (value.HasValue)
            {
                batch.SetRaw(in key, value.Value.AsSpan.WithoutLeadingZeros());
                return;
            }

            if (batch.TryGet(in key, out _))
            {
                batch.SetRaw(in key, ReadOnlySpan<byte>.Empty);
            }
        }

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) =>
            throw new NotSupportedException($"{nameof(FlatLayout.PaprikaFlat)} does not support account range deletion.");

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) =>
            throw new NotSupportedException($"{nameof(FlatLayout.PaprikaFlat)} does not support storage range deletion.");
    }
}

internal static class PaprikaNethermindCompatExtension
{
    public static PaprikaKeccak ToPaprikaKeccak(this in ValueHash256 hash) => new(hash.Bytes);
}
