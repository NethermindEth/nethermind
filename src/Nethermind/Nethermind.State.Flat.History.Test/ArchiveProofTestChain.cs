// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.History.Test;

/// <summary>
/// Builds one chain twice: as flat history rows, which is all a v2 archive keeps, and as a real state trie in a
/// MemDb that never prunes, so every historical root stays walkable and can answer the proof a node holding the
/// whole trie would have given.
/// </summary>
internal sealed class ArchiveProofTestChain(IColumnsDb<FlatHistoryColumns> historyColumns) : IHistoryHeaderSource, IDisposable
{
    private readonly IColumnsDb<FlatHistoryColumns> _historyColumns = historyColumns;
    private readonly MemDb _trieNodes = new();
    private readonly Dictionary<Address, Account> _accounts = [];
    private readonly Dictionary<Address, Dictionary<UInt256, byte[]>> _storage = [];
    private readonly Dictionary<ulong, ValueHash256> _rootsByBlock = [];
    private StateTree? _stateTree;

    private StateTree StateTree => _stateTree ??= new StateTree(new RawScopedTrieStore(_trieNodes), LimboLogs.Instance);

    public ulong Head { get; private set; }

    public ValueHash256? TryGetStateRoot(ulong block) =>
        _rootsByBlock.TryGetValue(block, out ValueHash256 root) ? root : null;

    public StateId StateIdAt(ulong block) => new(block, _rootsByBlock[block]);

    public void AddBlock(ulong block, Action<BlockBuilder> build)
    {
        BlockBuilder builder = new(this, block);
        build(builder);
        builder.Seal();

        StateTree.Commit();
        ValueHash256 root = new(StateTree.RootHash.Bytes);
        _rootsByBlock[block] = root;
        HistoryColumnsWriter.MarkBlock(_historyColumns, block, root);
        Head = block;
    }

    public void PublishWatermark() => HistoryColumnsWriter.SetWatermark(_historyColumns, Head);

    public AccountProof ExpectedProof(Address address, ulong block, params UInt256[] storageKeys)
    {
        AccountProofCollector collector = new(address, storageKeys);
        StateTree.Accept(collector, _rootsByBlock[block].ToCommitment());
        return collector.BuildResult();
    }

    public void Dispose() => _trieNodes.Dispose();

    internal sealed class BlockBuilder(ArchiveProofTestChain chain, ulong block)
    {
        private readonly Dictionary<Address, Account?> _accountChanges = [];
        private readonly HashSet<Address> _storageChanges = [];

        public BlockBuilder SetAccount(Address address, Account? account)
        {
            _accountChanges[address] = account;
            return this;
        }

        public BlockBuilder SetBalance(Address address, in UInt256 balance)
        {
            Account current = Current(address);
            return SetAccount(address, new Account(current.Nonce + 1, balance, current.StorageRoot, current.CodeHash));
        }

        public BlockBuilder SetStorage(Address address, in UInt256 slot, byte[] value)
        {
            if (!chain._storage.TryGetValue(address, out Dictionary<UInt256, byte[]>? slots))
            {
                slots = [];
                chain._storage[address] = slots;
            }

            slots[slot] = value;
            _storageChanges.Add(address);
            HistoryColumnsWriter.RecordStorage(chain._historyColumns, address, slot, block, value);
            return this;
        }

        public void Seal()
        {
            foreach (Address address in _storageChanges)
            {
                StorageTree storageTree = new(
                    new RawScopedTrieStore(chain._trieNodes, address.ToAccountPath.ToCommitment()),
                    Keccak.EmptyTreeHash,
                    LimboLogs.Instance);

                foreach ((UInt256 slot, byte[] value) in chain._storage[address])
                {
                    storageTree.Set(slot, value);
                }

                storageTree.Commit();

                Account owner = _accountChanges.TryGetValue(address, out Account? staged) && staged is not null ? staged : Current(address);
                _accountChanges[address] = new Account(owner.Nonce, owner.Balance, storageTree.RootHash, owner.CodeHash);
            }

            foreach ((Address address, Account? account) in _accountChanges)
            {
                if (account is null) chain._accounts.Remove(address);
                else chain._accounts[address] = account;

                chain.StateTree.Set(address, account);
                HistoryColumnsWriter.RecordAccount(chain._historyColumns, address, block, account);
            }
        }

        private Account Current(Address address) =>
            chain._accounts.TryGetValue(address, out Account? account) ? account : new Account(0, 0);
    }
}
