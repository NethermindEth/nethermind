// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Collections;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Persistence;
using Nethermind.Synchronization.FastSync;

namespace Nethermind.State.Pbt.Image;

/// <summary>Imports the native PBT state at this node's persisted hash-keyed flat state, with the keys resolved
/// through EIP-8347 preimages of an older anchor.</summary>
/// <remarks>
/// The preimages list the addresses and slot keys of the anchor's state. A key the flat state holds that the anchor
/// lacked was written by a block after the anchor, so the BALs of the blocks from the anchor up to the flat state list
/// it under the same account; they are spooled as a backup to the preimages. The flat state is pinned for the whole
/// import, and its accounts and slots are joined in Keccak path order with both sources: a key that neither lists
/// refuses the import. The joined leaves are then ingested as the preimage-flat import's are.
/// </remarks>
public sealed class PbtSelfImport(
    IPersistence flatPersistence,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IColumnsDb<PbtColumns> pbtDb,
    PbtRocksDbPersistence target,
    PbtPersistenceManager persistenceManager,
    PbtLeafIngestion leafIngestion,
    IBlockTree blockTree,
    BalFetcher balFetcher,
    IBlockAccessListStore balStore,
    IDbFactory dbFactory,
    IPbtConfig config,
    ILogManager logManager)
{
    /// <summary>Keccak address path, the account/slot tag, then the Keccak slot path.</summary>
    private const int BalKeyLength = 65;

    /// <summary>The Keccak address path bytes a hash-keyed flat account key keeps.</summary>
    private const int FlatAccountKeyLength = Address.Size;

    private const string JoinPhase = "PBT self import scan";

    private readonly ILogger _logger = logManager.GetClassLogger<PbtSelfImport>();

    /// <param name="preimages">The EIP-8347 preimages of <paramref name="anchor"/>.</param>
    /// <param name="anchor">The canonical block whose state <paramref name="preimages"/> list.</param>
    public async Task Import(Stream preimages, BlockHeader anchor, CancellationToken cancellationToken)
    {
        if (target.IsValid)
        {
            if (_logger.IsInfo) _logger.Info("PBT state already populated; skipping the self import.");
            return;
        }

        Stopwatch importing = Stopwatch.StartNew();
        // The one snapshot pins the source for the whole import while the live flat state moves on.
        using IPersistence.IPersistenceReader reader = flatPersistence.CreateReader(ReaderFlags.FullScan);
        StateId sourceState = reader.CurrentState;
        BlockHeader source = blockTree.FindHeader(sourceState.BlockNumber, BlockTreeLookupOptions.RequireCanonical) is { } header &&
            header.StateRoot?.ValueHash256 == sourceState.StateRoot
                ? header
                : throw new InvalidOperationException($"The persisted flat state {sourceState} is not on the canonical chain.");
        if (_logger.IsInfo) _logger.Info($"Importing PBT state at the flat state {sourceState} through the preimages of {anchor.ToString(BlockHeader.Format.Short)}.");

        if (PbtColumnSweep.ClearInterruptedImport(pbtDb) && _logger.IsInfo) _logger.Info("Discarded entries left by an interrupted PBT import.");
        string directory = Path.Combine(PbtMigrationAnchor.ScratchDirectory(dbFactory, "import"), $"pbt-self-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        (ValueHash256 Root, ulong Accounts, ulong Slots) imported;
        try
        {
            using SortedSpool balKeys = new(directory, config.ImportSortBufferBytes, writerCount: 1, logManager, cancellationToken);
            await SpoolBalKeys(balKeys, anchor, source, cancellationToken);
            using SortedSpool leaves = new(directory, config.ImportSortBufferBytes, writerCount: 1, logManager, cancellationToken);
            Join(reader, preimages, balKeys, leaves, cancellationToken);
            imported = await leafIngestion.Ingest(_ => PbtLeafIngestion.SpoolLeaves(leaves), sourceState, expectedRoot: null, VerifyCanonical, cancellationToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        foreach (PbtColumns column in pbtDb.ColumnKeys) pbtDb.GetColumnDb(column).SyncWal();
        // The import bypassed the live persistence: reload what it cached before the write.
        persistenceManager.ResetPersistedStateId();
        if (_logger.IsInfo)
            _logger.Info($"Imported the PBT state at {source.ToString(BlockHeader.Format.Short)} with root {imported.Root}: " +
                $"{imported.Accounts:N0} accounts and {imported.Slots:N0} slots in {importing.Elapsed:hh\\:mm\\:ss}.");

        void VerifyCanonical(CancellationToken _)
        {
            if (!blockTree.IsMainChain(source)) throw new InvalidOperationException("The flat state left the canonical chain during the self import.");
        }
    }

    /// <summary>Spools the accounts and slot keys the BALs of the blocks after <paramref name="anchor"/> up to <paramref name="source"/> list.</summary>
    private async Task SpoolBalKeys(SortedSpool keys, BlockHeader anchor, BlockHeader source, CancellationToken cancellationToken)
    {
        if (source.Number <= anchor.Number) return;
        if (blockTree.FindHeader(anchor.Number + 1, BlockTreeLookupOptions.RequireCanonical)?.BlockAccessListHash is null)
            throw new InvalidDataException("The preimages' anchor precedes EIP-7928, so no BALs list the keys written since.");
        if (!await balFetcher.EnsureRange(anchor, source, cancellationToken))
            throw new InvalidOperationException("The BALs from the preimages' anchor up to the flat state are unavailable.");

        using SortedSpool.Writer writer = keys.CreateWriter();
        byte[] key = new byte[BalKeyLength];
        for (ulong number = anchor.Number + 1; number <= source.Number; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlockHeader header = blockTree.FindHeader(number, BlockTreeLookupOptions.RequireCanonical)
                ?? throw new InvalidOperationException($"Block {number} is no longer canonical.");
            ReadOnlyBlockAccessList bal;
            using (MemoryManager<byte>? rlp = balStore.GetRlp(number, header.Hash!))
                bal = (rlp is null ? null : PbtBalFollower.DecodeAuthenticated(header, rlp.Memory.Span))
                    ?? throw new InvalidOperationException($"No BAL matching the header of {header.ToString(BlockHeader.Format.Short)} is stored.");

            foreach (ReadOnlyAccountChanges account in bal.AccountChanges)
            {
                // The zero tag and slot-hash region keep an account ahead of its own slots.
                Array.Clear(key);
                ValueKeccak.Compute(account.Address.Bytes).Bytes.CopyTo(key);
                writer.Add(key, account.Address.Bytes);
                key[ValueHash256.MemorySize] = 1;
                foreach (UInt256 slot in account.ChangedSlots) AddSlot(slot);
                foreach (UInt256 slot in account.StorageReads) AddSlot(slot);
            }
        }

        void AddSlot(in UInt256 slot)
        {
            ValueHash256 slotKey = slot.ToValueHash();
            ValueKeccak.Compute(slotKey.Bytes).Bytes.CopyTo(key.AsSpan(ValueHash256.MemorySize + 1));
            writer.Add(key, slotKey.Bytes);
        }
    }

    /// <summary>Spools the leaves of every account and slot of <paramref name="source"/>, resolving their Keccak paths to preimages.</summary>
    private void Join(IPersistence.IPersistenceReader source, Stream preimages, SortedSpool balKeys, SortedSpool leaves, CancellationToken cancellationToken)
    {
        ulong accounts = 0, slots = 0;
        float walked = 0;
        using ProgressReporter progress = PbtImageProgress.Start(JoinPhase, "acc", 0, logManager);
        Func<string> slotCounter = PbtImageProgress.Counter("slot", () => slots);
        progress.Logger.SetFormat(p => PbtImageProgress.Format(JoinPhase, walked, $"{PbtImageProgress.Counted("acc", p)} | {slotCounter()}"));

        using PreimageJoin listed = new(preimages, balKeys, cancellationToken);
        using SortedSpool.Writer writer = leaves.CreateWriter();
        using IPersistence.IFlatIterator flatAccounts = source.CreateAccountIterator(default, ValueKeccak.MaxValue);
        while (flatAccounts.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RlpReader rlp = new(flatAccounts.CurrentValue);
            Account account = AccountDecoder.Slim.Decode(ref rlp) ?? throw new InvalidDataException("Invalid source account.");
            Address address = listed.Account(flatAccounts.CurrentKey, out ValueHash256 addressHash);
            ValueHash256 addressKeyHash = Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes);
            PbtOfflineSource.SpoolAccount(writer, codeDb, addressKeyHash, account);
            using (IPersistence.IFlatIterator flatSlots = source.CreateStorageIterator(addressHash, default, ValueKeccak.MaxValue))
            {
                while (flatSlots.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PbtOfflineSource.SpoolSlot(writer, address, addressKeyHash, listed.Slot(flatSlots.CurrentKey), new UInt256(flatSlots.CurrentValue, isBigEndian: true));
                    slots++;
                }
            }
            walked = PbtImageProgress.KeyspaceFraction(addressHash);
            progress.Update(++accounts);
        }
    }

    /// <summary>Resolves ascending Keccak paths to their preimages, from the preimage stream or else the spooled BAL keys.</summary>
    /// <remarks>Both sources ascend in Keccak path order, as the flat walk does, so each only moves forward.</remarks>
    private sealed class PreimageJoin : IDisposable
    {
        private readonly PbtPreimageReader _preimages;
        private readonly SortedSpool.Cursor _balKeys;
        private readonly CancellationToken _cancellationToken;
        private readonly byte[] _balKey = new byte[BalKeyLength];
        private bool _hasBalKey;
        private Address? _listedAddress;
        private uint _unreadListedSlots;
        private bool _listedIsCurrent;
        private ValueHash256? _listedSlotHash;
        private ValueHash256 _listedSlot;
        private ValueHash256 _addressHash;

        public PreimageJoin(Stream preimages, SortedSpool balKeys, CancellationToken cancellationToken)
        {
            preimages.Position = 0;
            _preimages = new PbtPreimageReader(preimages);
            _balKeys = balKeys.Read();
            _cancellationToken = cancellationToken;
            _hasBalKey = _balKeys.MoveNext();
            NextListedAccount();
        }

        /// <summary>The address under the flat account key <paramref name="flatKey"/>, which keeps only a prefix of its Keccak path.</summary>
        /// <param name="addressHash">The whole Keccak path of the address.</param>
        public Address Account(in ValueHash256 flatKey, out ValueHash256 addressHash)
        {
            ReadOnlySpan<byte> prefix = flatKey.Bytes[..FlatAccountKeyLength];
            while (_listedAddress is not null && _preimages.AccountHash!.Value.Bytes[..FlatAccountKeyLength].SequenceCompareTo(prefix) < 0)
                NextListedAccount();

            _listedIsCurrent = _listedAddress is not null && _preimages.AccountHash!.Value.Bytes[..FlatAccountKeyLength].SequenceEqual(prefix);
            Address address;
            if (_listedIsCurrent)
            {
                address = _listedAddress!;
                _addressHash = _preimages.AccountHash!.Value;
                NextListedSlot();
            }
            else
            {
                Array.Clear(_balKey);
                prefix.CopyTo(_balKey);
                SeekBalKey();
                if (!_hasBalKey || !_balKeys.Key[..FlatAccountKeyLength].SequenceEqual(prefix) || _balKeys.Key[ValueHash256.MemorySize] != 0)
                    throw new InvalidDataException($"Flat account {prefix.ToHexString()} has no preimage.");
                address = new Address(_balKeys.Value);
                _addressHash = new ValueHash256(_balKeys.Key[..ValueHash256.MemorySize]);
            }
            addressHash = _addressHash;
            return address;
        }

        /// <summary>The slot key of the current account at the Keccak path <paramref name="slotHash"/>.</summary>
        public UInt256 Slot(in ValueHash256 slotHash)
        {
            if (_listedIsCurrent)
            {
                while (_listedSlotHash is { } listedHash && listedHash.Bytes.SequenceCompareTo(slotHash.Bytes) < 0) NextListedSlot();
                if (_listedSlotHash == slotHash) return new UInt256(_listedSlot.Bytes, isBigEndian: true);
            }

            _addressHash.Bytes.CopyTo(_balKey);
            _balKey[ValueHash256.MemorySize] = 1;
            slotHash.Bytes.CopyTo(_balKey.AsSpan(ValueHash256.MemorySize + 1));
            SeekBalKey();
            if (!_hasBalKey || !_balKeys.Key.SequenceEqual(_balKey))
                throw new InvalidDataException($"Flat slot {slotHash} of account {_addressHash} has no preimage.");
            return new UInt256(_balKeys.Value, isBigEndian: true);
        }

        /// <summary>Moves the BAL key cursor to the first key at or past <see cref="_balKey"/>.</summary>
        private void SeekBalKey()
        {
            while (_hasBalKey && _balKeys.Key.SequenceCompareTo(_balKey) < 0) _hasBalKey = _balKeys.MoveNext();
        }

        private void NextListedAccount()
        {
            while (_unreadListedSlots > 0) NextListedSlot();
            _listedSlotHash = null;
            if (!_preimages.ReadAccount(out _listedAddress, out _unreadListedSlots, _cancellationToken)) _listedAddress = null;
        }

        private void NextListedSlot()
        {
            if (_unreadListedSlots == 0)
            {
                _listedSlotHash = null;
                return;
            }
            _listedSlot = _preimages.ReadSlot(_cancellationToken);
            _listedSlotHash = _preimages.SlotHash;
            _unreadListedSlots--;
        }

        public void Dispose() => _balKeys.Dispose();
    }
}
