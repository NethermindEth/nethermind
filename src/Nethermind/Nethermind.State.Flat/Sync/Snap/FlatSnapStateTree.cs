// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTree adapter for flat snap sync (state).
/// Owns reader (for IsPersisted) and writeBatch (for commits), disposing them on Dispose.
/// </summary>
public class FlatSnapStateTree : ISnapTree<PathWithAccount>, SnapBoundaryCleaner.IRangeDeleter
{
    private readonly IPersistence.IPersistenceReader _reader;
    private readonly IPersistence.IWriteBatch _writeBatch;
    private readonly bool _enableDoubleWriteCheck;
    private readonly SnapUpperBoundAdapter _adapter;
    private readonly StateTree _tree;
    private IReadOnlyList<PathWithAccount>? _pendingEntries;
    internal Action<ValueHash256, ValueHash256, bool>? BeforeCleanup { get; init; }

    public FlatSnapStateTree(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, bool enableDoubleWriteCheck, ILogManager logManager)
    {
        _reader = reader;
        _writeBatch = writeBatch;
        _enableDoubleWriteCheck = enableDoubleWriteCheck;
        _adapter = new SnapUpperBoundAdapter(new PersistenceTrieStoreAdapter(reader, writeBatch, enableDoubleWriteCheck));
        _tree = new StateTree(_adapter, logManager);
    }

    public Hash256 RootHash => _tree.RootHash;

    public void SetRootFromProof(TrieNode root) => _tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        ValueHash256 computedHash = default;
        return _reader.TryLoadStateRlp(path, ReadFlags.None, static (rlp, result) => result[0] = ValueKeccak.Compute(rlp), new Span<ValueHash256>(ref computedHash))
            && computedHash == keccak;
    }

    public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithAccount> entries)
    {
        _pendingEntries = entries;
        ISnapTree<PathWithAccount>.DoBulkSetAndUpdateRootHash(_tree, entries);
    }

    public void Commit(ValueHash256 upperBound)
    {
        _adapter.UpperBound = upperBound;

        if (_pendingEntries is not null)
        {
            for (int i = 0; i < _pendingEntries.Count; i++)
            {
                PathWithAccount account = _pendingEntries[i];
                if (account.Path <= upperBound)
                {
                    if (_enableDoubleWriteCheck && _reader.GetAccountRaw(account.Path) is not null)
                        throw new Exception($"Double account flat write. {account.Path}");
                    if (account.Account.IsNull)
                        throw new InvalidOperationException("A SNAP account entry must contain an account value.");
                    _writeBatch.SetAccountRaw(account.Path, account.Account);
                }
            }
        }

        _tree.Commit(true, WriteFlags.DisableWAL);
    }

    /// <inheritdoc/>
    public void DeleteOutsideChildren(in TreePath path, TrieNode node, in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd) =>
        SnapBoundaryCleaner.DeleteOutsideChildren(path, node, this, cleanupStart, cleanupEnd);

    /// <inheritdoc/>
    public void DeleteOutsideCompleteTree(in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd) =>
        SnapBoundaryCleaner.DeleteOutsideCompleteTree(TreePath.Empty, _tree.RootRef, this, cleanupStart, cleanupEnd);

    void SnapBoundaryCleaner.IRangeDeleter.DeleteNode(in TreePath path)
    {
        if (BeforeCleanup is { } before && _reader.TryLoadStateRlp(path, ReadFlags.None, static (_, _) => { }, 0))
            before(path.ToLowerBoundPath(), path.ToUpperBoundPath(), true);
        _writeBatch.DeleteStateTrieNode(path);
    }

    byte[]? SnapBoundaryCleaner.IRangeDeleter.LoadNode(in TreePath path)
    {
        byte[]? rlp = null;
        _reader.TryLoadStateRlp(path, ReadFlags.None, static (value, result) => result[0] = value.ToArray(), new Span<byte[]?>(ref rlp));
        return rlp;
    }

    /// <remarks>
    /// An account that the verified root no longer has also loses its flat storage. Flat accounts and storage are keyed
    /// by the first <see cref="BaseFlatPersistence.AccountKeyLength"/> bytes of the account path, so both deletes are
    /// narrowed to the account keys that lie wholly inside the range: a range beside a kept leaf never reaches the kept
    /// account or its storage. Trie nodes are keyed by the whole path and use the exact range.
    /// </remarks>
    void SnapBoundaryCleaner.IRangeDeleter.DeleteRange(in ValueHash256 from, in ValueHash256 to)
    {
        BeforeCleanup?.Invoke(from, to, false);
        _writeBatch.DeleteStateTrieNodeRange(from, to);
        if (TryGetWholeAccountKeys(from, to, out ValueHash256 firstAccount, out ValueHash256 lastAccount))
        {
            _writeBatch.DeleteAccountRange(firstAccount, lastAccount);
            _writeBatch.SelfDestructRange(firstAccount, lastAccount, null);
        }
    }

    /// <summary>
    /// Narrows <c>[<paramref name="from"/>, <paramref name="to"/>]</c> to the account keys, the first
    /// <see cref="BaseFlatPersistence.AccountKeyLength"/> bytes of a path, that lie wholly inside it.
    /// </summary>
    /// <returns><c>false</c> when no whole account key lies inside the range.</returns>
    internal static bool TryGetWholeAccountKeys(in ValueHash256 from, in ValueHash256 to, out ValueHash256 firstAccount, out ValueHash256 lastAccount)
    {
        const int keyLength = BaseFlatPersistence.AccountKeyLength;
        firstAccount = from;
        lastAccount = to;
        bool fromIsKeyStart = !from.Bytes[keyLength..].ContainsAnyExcept(byte.MinValue);
        bool toIsKeyEnd = !to.Bytes[keyLength..].ContainsAnyExcept(byte.MaxValue);
        if (!fromIsKeyStart && !TryStepAccountKey(from, up: true, out firstAccount)) return false;
        if (!toIsKeyEnd && !TryStepAccountKey(to, up: false, out lastAccount)) return false;
        return firstAccount.CompareTo(lastAccount) <= 0;
    }

    private static bool TryStepAccountKey(in ValueHash256 path, bool up, out ValueHash256 result)
    {
        const int keyLength = BaseFlatPersistence.AccountKeyLength;
        Span<byte> bytes = stackalloc byte[ValueHash256.MemorySize];
        path.Bytes[..keyLength].CopyTo(bytes);
        bytes[keyLength..].Fill(up ? byte.MinValue : byte.MaxValue);
        for (int i = keyLength - 1; i >= 0; i--)
        {
            byte previous = bytes[i];
            bytes[i] = up ? (byte)(previous + 1) : (byte)(previous - 1);
            if (previous != (up ? byte.MaxValue : byte.MinValue))
            {
                result = new ValueHash256(bytes);
                return true;
            }
        }

        result = default;
        return false;
    }

    public void Dispose()
    {
        _writeBatch.Dispose();
        _reader.Dispose();
    }
}
