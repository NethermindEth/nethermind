// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

/// <summary>
/// Verifies flat storage against trie by iterating flat storage entries and comparing with trie values.
/// This complements FlatVerifyTrieVisitor which does the reverse direction (trie -> flat).
/// </summary>
public class FlatToTrieVerifier
{
    private readonly IPersistence.IPersistenceReader _reader;
    private readonly IScopedTrieStore _trieStore;
    private readonly Hash256 _stateRoot;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;
    private long _lastLoggedCount;

    public FlatToTrieVerifier(
        IPersistence.IPersistenceReader reader,
        IScopedTrieStore trieStore,
        Hash256 stateRoot,
        ILogManager logManager,
        CancellationToken cancellationToken)
    {
        _reader = reader;
        _trieStore = trieStore;
        _stateRoot = stateRoot;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<FlatToTrieVerifier>();
        _cancellationToken = cancellationToken;
    }

    public VerificationStats Stats { get; } = new();

    public void Verify()
    {
        if (!_reader.IsPreimageMode)
        {
            if (_logger.IsInfo) _logger.Info("FlatToTrie: Running in non-preimage mode. Storage verification will be skipped (requires full 32-byte address hash).");
        }

        StateTree stateTree = new StateTree(_trieStore, _logManager);
        stateTree.RootHash = _stateRoot;

        using IPersistence.IFlatIterator accountIterator = _reader.CreateAccountIterator();

        while (accountIterator.MoveNext())
        {
            _cancellationToken.ThrowIfCancellationRequested();

            ValueHash256 accountKey = accountIterator.CurrentKey;
            ReadOnlySpan<byte> flatAccountRlp = accountIterator.CurrentValue;

            Interlocked.Increment(ref Stats._accountCount);

            LogProgress();

            // If preimage mode, we need to hash the key to get the full trie path
            // In non-preimage mode, we only have the truncated 20-byte hash
            ValueHash256 triePath;
            ReadOnlySpan<byte> trieAccountRlp;
            if (_reader.IsPreimageMode)
            {
                // Preimage mode: hash the raw address to get full 32-byte path
                triePath = ValueKeccak.Compute(accountKey.Bytes[..20]);
                trieAccountRlp = stateTree.Get(triePath.Bytes);
            }
            else
            {
                // Non-preimage mode: use partial key matching with the 20-byte truncated hash
                // The triePath for storage lookups still needs to be the accountKey
                triePath = accountKey;
                trieAccountRlp = stateTree.GetWithPartialKey(accountKey.Bytes[..20]);
            }

            if (trieAccountRlp.IsEmpty)
            {
                if (_logger.IsWarn) _logger.Warn($"FlatToTrie: Account in flat not found in trie. Key: {accountKey}");
                Interlocked.Increment(ref Stats._missingInTrie);
                continue;
            }

            // Compare the RLP bytes - need to handle slim vs standard account encoding
            if (!CompareAccountRlp(flatAccountRlp, trieAccountRlp, accountKey))
            {
                Interlocked.Increment(ref Stats._mismatchedAccount);
            }

            // Verify storage for this account
            VerifyAccountStorage(accountKey, triePath);
        }
    }

    private bool CompareAccountRlp(ReadOnlySpan<byte> flatRlp, ReadOnlySpan<byte> trieRlp, in ValueHash256 accountKey)
    {
        // Decode both accounts and compare semantically since encoding may differ
        // (flat uses slim encoding, trie uses standard encoding)
        try
        {
            Rlp.ValueDecoderContext flatCtx = new Rlp.ValueDecoderContext(flatRlp);
            Account? flatAccount = AccountDecoder.Slim.Decode(ref flatCtx);

            Rlp.ValueDecoderContext trieCtx = new Rlp.ValueDecoderContext(trieRlp);
            Account? trieAccount = AccountDecoder.Instance.Decode(ref trieCtx);

            if (flatAccount != trieAccount)
            {
                if (_logger.IsWarn) _logger.Warn($"FlatToTrie: Mismatched account. Key: {accountKey}. Flat: {flatAccount}, Trie: {trieAccount}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) _logger.Warn($"FlatToTrie: Error decoding account. Key: {accountKey}. Error: {ex.Message}");
            return false;
        }
    }

    private void VerifyAccountStorage(in ValueHash256 accountKey, in ValueHash256 triePath)
    {
        // In non-preimage mode, we only have the 20-byte truncated address hash,
        // but the storage trie resolver requires the full 32-byte hash to identify
        // which storage trie to use. Skip storage verification in non-preimage mode.
        if (!_reader.IsPreimageMode)
        {
            return;
        }

        using IPersistence.IFlatIterator storageIterator = _reader.CreateStorageIterator(accountKey);

        // Get storage root from trie for this account
        byte[]? accountRlp = _reader.GetAccountRaw(new Hash256(triePath));
        if (accountRlp is null) return;

        Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(accountRlp);
        Account? account = AccountDecoder.Slim.Decode(ref ctx);
        if (account is null || account.StorageRoot == Keccak.EmptyTreeHash) return;

        IScopedTrieStore storageTrieStore = (IScopedTrieStore)_trieStore.GetStorageTrieNodeResolver(new Hash256(triePath));
        StorageTree storageTree = new StorageTree(storageTrieStore, account.StorageRoot, _logManager);

        while (storageIterator.MoveNext())
        {
            _cancellationToken.ThrowIfCancellationRequested();

            ValueHash256 slotKey = storageIterator.CurrentKey;
            ReadOnlySpan<byte> flatValue = storageIterator.CurrentValue;

            Interlocked.Increment(ref Stats._slotCount);

            // If preimage mode, hash the slot key
            ValueHash256 trieSlotPath = _reader.IsPreimageMode
                ? ValueKeccak.Compute(slotKey.Bytes)
                : slotKey;

            // Look up slot in storage trie - use base PatriciaTree.Get which takes raw key bytes
            ReadOnlySpan<byte> trieValue = storageTree.Get(trieSlotPath.Bytes);

            if (trieValue.IsEmpty)
            {
                // Flat value should also be zero/empty
                if (!IsZeroValue(flatValue))
                {
                    if (_logger.IsWarn) _logger.Warn($"FlatToTrie: Storage slot in flat not found in trie. Account: {accountKey}, Slot: {slotKey}, FlatValue: {flatValue.ToHexString()}");
                    Interlocked.Increment(ref Stats._missingInTrie);
                }
                continue;
            }

            // Compare values - flat stores without leading zeros, trie stores RLP encoded
            if (!CompareStorageValues(flatValue, trieValue, accountKey, slotKey))
            {
                Interlocked.Increment(ref Stats._mismatchedSlot);
            }
        }
    }

    private bool CompareStorageValues(ReadOnlySpan<byte> flatValue, ReadOnlySpan<byte> trieValue, in ValueHash256 accountKey, in ValueHash256 slotKey)
    {
        // Trie value is RLP encoded, flat value is raw bytes without leading zeros
        Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(trieValue);
        byte[] decodedTrieValue = ctx.DecodeByteArray();

        // Both should be without leading zeros for comparison
        ReadOnlySpan<byte> flatTrimmed = flatValue.WithoutLeadingZeros();
        ReadOnlySpan<byte> trieTrimmed = decodedTrieValue.AsSpan().WithoutLeadingZeros();

        if (!Bytes.AreEqual(flatTrimmed, trieTrimmed))
        {
            if (_logger.IsWarn) _logger.Warn($"FlatToTrie: Mismatched storage. Account: {accountKey}, Slot: {slotKey}. Flat: {flatTrimmed.ToHexString()}, Trie: {trieTrimmed.ToHexString()}");
            return false;
        }

        return true;
    }

    private static bool IsZeroValue(ReadOnlySpan<byte> value)
    {
        return value.IsEmpty || value.WithoutLeadingZeros().IsEmpty;
    }

    private void LogProgress()
    {
        long current = Stats.AccountCount;
        long last = _lastLoggedCount;
        if (current - last > 100_000 && Interlocked.CompareExchange(ref _lastLoggedCount, current, last) == last)
        {
            _logger.Warn($"FlatToTrie verification: Checked {Stats.AccountCount} accounts, {Stats.SlotCount} slots. " +
                        $"Missing: {Stats.MissingInTrie}, Mismatched accounts: {Stats.MismatchedAccount}, Mismatched slots: {Stats.MismatchedSlot}");
        }
    }

    public class VerificationStats
    {
        internal long _accountCount;
        internal long _slotCount;
        internal long _mismatchedAccount;
        internal long _mismatchedSlot;
        internal long _missingInTrie;

        public long AccountCount => _accountCount;
        public long SlotCount => _slotCount;
        public long MismatchedAccount => _mismatchedAccount;
        public long MismatchedSlot => _mismatchedSlot;
        public long MissingInTrie => _missingInTrie;

        public override string ToString()
        {
            return $"FlatToTrie Stats: Accounts={AccountCount}, Slots={SlotCount}, MismatchedAccounts={MismatchedAccount}, MismatchedSlots={MismatchedSlot}, MissingInTrie={MissingInTrie}";
        }
    }
}
