// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
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
    private readonly CancellationToken _cancellationToken;
    private readonly ILogger _logger;
    private readonly VisitorProgressTracker _progressTracker;

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
        _cancellationToken = cancellationToken;
        _logger = logManager.GetClassLogger<FlatToTrieVerifier>();
        _progressTracker = new VisitorProgressTracker("Flat->Trie Verify", logManager);
    }

    public VerificationStats Stats { get; } = new();

    public void Verify()
    {
        StateTree stateTree = new StateTree(_trieStore, _logManager);
        stateTree.RootHash = _stateRoot;

        using IPersistence.IFlatIterator accountIterator = _reader.CreateAccountIterator();

        while (accountIterator.MoveNext())
        {
            _cancellationToken.ThrowIfCancellationRequested();

            ValueHash256 accountKey = accountIterator.CurrentKey;
            ReadOnlySpan<byte> flatAccountRlp = accountIterator.CurrentValue;

            Interlocked.Increment(ref Stats._accountCount);

            // Track progress using the account key's first 4 nibbles as a synthetic path
            TreePath accountPath = TreePath.FromNibble(accountKey.Bytes[..2]);
            _progressTracker.OnNodeVisited(accountPath, isStorage: false, isLeaf: true);

            // If preimage mode, we need to hash the key to get the full trie path
            // In non-preimage mode, we only have the truncated 20-byte hash
            ValueHash256 triePath;
            byte[]? trieAccountRlp;
            if (_reader.IsPreimageMode)
            {
                // Preimage mode: hash the raw address to get full 32-byte path
                triePath = ValueKeccak.Compute(accountKey.Bytes[..20]);
                trieAccountRlp = stateTree.Get(triePath.Bytes).ToArray();
            }
            else
            {
                // Non-preimage mode: use partial key matching with the 20-byte truncated hash
                // and get the full 32-byte path from the trie for storage lookups
                trieAccountRlp = GetWithPartialKeyAndFullPath(stateTree, accountKey.Bytes[..20], out triePath);
            }

            if (trieAccountRlp is null || trieAccountRlp.Length == 0)
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

        _progressTracker.Finish();
    }

    /// <summary>
    /// Gets value from trie using partial key and returns the full 32-byte path.
    /// This walks the trie to find a leaf matching the partial key prefix and extracts the full path.
    /// </summary>
    private static byte[]? GetWithPartialKeyAndFullPath(StateTree stateTree, ReadOnlySpan<byte> partialKey, out ValueHash256 fullPath)
    {
        fullPath = default;

        // Convert partial key to nibbles
        int nibblesCount = 2 * partialKey.Length;
        byte[]? array = nibblesCount > 64 ? ArrayPool<byte>.Shared.Rent(nibblesCount) : null;
        Span<byte> nibbles = array is not null ? array.AsSpan(0, nibblesCount) : stackalloc byte[nibblesCount];

        try
        {
            Nibbles.BytesToNibbleBytes(partialKey, nibbles);

            TreePath path = TreePath.Empty;
            TrieNode? node = stateTree.RootRef;

            while (node is not null)
            {
                node.ResolveNode(stateTree.TrieStore, path);

                if (node.IsLeaf)
                {
                    // Check if partial key matches
                    int commonPrefix = nibbles.CommonPrefixLength(node.Key);
                    if (commonPrefix == nibbles.Length)
                    {
                        // Match found - construct full path
                        path = path.Append(node.Key);
                        fullPath = ConstructFullPath(path);
                        return node.Value.ToArray();
                    }
                    return null;
                }

                if (node.IsExtension)
                {
                    int commonPrefix = nibbles.CommonPrefixLength(node.Key);
                    if (commonPrefix == node.Key!.Length)
                    {
                        // Continue through extension
                        path = path.Append(node.Key);
                        nibbles = nibbles[node.Key.Length..];
                        node = node.GetChildWithChildPath(stateTree.TrieStore, ref path, 0);
                        continue;
                    }
                    else if (commonPrefix == nibbles.Length)
                    {
                        // Partial key consumed within extension - find first leaf
                        path = path.Append(node.Key);
                        node = node.GetChildWithChildPath(stateTree.TrieStore, ref path, 0);
                        return GetFirstLeafWithPath(stateTree, path, node, out fullPath);
                    }
                    return null;
                }

                // Branch node
                if (nibbles.Length == 0)
                {
                    // Partial key consumed - find first leaf
                    return GetFirstLeafWithPath(stateTree, path, node, out fullPath);
                }

                int nib = nibbles[0];
                path.AppendMut(nib);
                node = node.GetChildWithChildPath(stateTree.TrieStore, ref path, nib);
                nibbles = nibbles[1..];
            }

            return null;
        }
        finally
        {
            if (array is not null) ArrayPool<byte>.Shared.Return(array);
        }
    }

    private static byte[]? GetFirstLeafWithPath(StateTree stateTree, TreePath path, TrieNode? node, out ValueHash256 fullPath)
    {
        fullPath = default;

        while (node is not null)
        {
            node.ResolveNode(stateTree.TrieStore, path);

            if (node.IsLeaf)
            {
                path = path.Append(node.Key);
                fullPath = ConstructFullPath(path);
                return node.Value.ToArray();
            }

            if (node.IsExtension)
            {
                path = path.Append(node.Key);
                node = node.GetChildWithChildPath(stateTree.TrieStore, ref path, 0);
                continue;
            }

            // Branch - find first non-null child
            for (int i = 0; i < 16; i++)
            {
                path = path.Append(i);
                TrieNode? child = node.GetChildWithChildPath(stateTree.TrieStore, ref path, i);
                if (child is not null)
                {
                    node = child;
                    break;
                }
                path = path.Truncate(path.Length - 1);
            }
        }

        return null;
    }

    private static ValueHash256 ConstructFullPath(TreePath path)
    {
        // TreePath contains nibbles, convert to 32-byte hash
        byte[] bytes = Nibbles.ToBytes(path);
        return new ValueHash256(bytes);
    }

    private bool CompareAccountRlp(ReadOnlySpan<byte> flatRlp, ReadOnlySpan<byte> trieRlp, in ValueHash256 accountKey)
    {
        // Decode both accounts and compare semantically since encoding may differ
        // (flat uses slim encoding, trie uses standard encoding)
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

    private void VerifyAccountStorage(in ValueHash256 accountKey, in ValueHash256 triePath)
    {
        using IPersistence.IFlatIterator storageIterator = _reader.CreateStorageIterator(accountKey);

        // Get storage root from the account
        Account? account;
        if (_reader.IsPreimageMode)
        {
            // In preimage mode, accountKey contains the raw address (20 bytes)
            Address address = new Address(accountKey.Bytes[..20].ToArray());
            account = _reader.GetAccount(address);
        }
        else
        {
            // In non-preimage mode, use GetAccountRaw with the hash
            byte[]? accountRlp = _reader.GetAccountRaw(new Hash256(triePath));
            if (accountRlp is null) return;
            Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(accountRlp);
            account = AccountDecoder.Slim.Decode(ref ctx);
        }

        if (account is null || account.StorageRoot == Keccak.EmptyTreeHash) return;

        IScopedTrieStore storageTrieStore = (IScopedTrieStore)_trieStore.GetStorageTrieNodeResolver(new Hash256(triePath));
        StorageTree storageTree = new StorageTree(storageTrieStore, account.StorageRoot, _logManager);

        while (storageIterator.MoveNext())
        {
            _cancellationToken.ThrowIfCancellationRequested();

            ValueHash256 slotKey = storageIterator.CurrentKey;
            ReadOnlySpan<byte> flatValue = storageIterator.CurrentValue;

            Interlocked.Increment(ref Stats._slotCount);

            // In preimage mode, hash the slot key to get the trie path
            // In non-preimage mode, the slotKey is already the hashed path
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
