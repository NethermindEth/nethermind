// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.State;
using Nethermind.Trie.Pruning;

public class AccountTreeMigrator : ITreeVisitor
{
    private readonly VerkleStateTree _verkleStateTree;
    private readonly IStateReader _stateReader;
    private readonly TrieStore _trieStore;
    private int _accountsProcessed = 0;

    public AccountTreeMigrator(VerkleStateTree verkleStateTree, IStateReader stateReader, TrieStore trieStore)
    {
        _verkleStateTree = verkleStateTree;
        _stateReader = stateReader;
        _trieStore = trieStore;
    }

    public bool IsFullDbScan => true;

    public bool ShouldVisit(Hash256 nodeHash) => true;

    public void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext)
    {
        Console.WriteLine($"Starting migration from Merkle tree with root: {rootHash}");
    }

    public void VisitMissingNode(Hash256 nodeHash, TrieVisitContext trieVisitContext)
    {
        Console.WriteLine($"Warning: Missing node encountered: {nodeHash}");
    }

    public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext) { }

    public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext) { }

    private readonly AccountDecoder decoder = new();

    public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
    {
        if (!trieVisitContext.IsStorage)
        {
            Rlp.ValueDecoderContext valueDecoderContext = new(value);

            // Assume the address is the last 20 bytes of the path
            // TODO: verify if this address is correct
            Nibble[] fullPath = Nibbles.FromBytes(node.Key);
            var address = new Address(Nibbles.ToPackedByteArray(fullPath)[..20]);
            Account account = decoder.Decode(ref valueDecoderContext);

            MigrateAccount(address, account);

            if (account.IsContract)
            {
                MigrateContractCode(address, account.CodeHash);
            }

            MigrateAccountStorage(address, account.StorageRoot);

            _accountsProcessed++;
        }
    }

    public void VisitCode(Hash256 codeHash, TrieVisitContext trieVisitContext) { }

    private void MigrateAccount(Address address, Account account)
    {
        _verkleStateTree.Set(address, account);
    }

    private void MigrateContractCode(Address address, Hash256 codeHash)
    {
        byte[] code = _stateReader.GetCode(codeHash);
        _verkleStateTree.SetCode(address, code);
    }

    private void MigrateAccountStorage(Address address, Hash256 storageRoot)
    {
            var storageTree = new StorageTree(_trieStore.GetTrieStore(address.ToAccountPath), storageRoot, null);
            var storageVisitor = new StorageTreeMigrator(_verkleStateTree, address);
            storageTree.Accept(storageVisitor, storageRoot);
            storageVisitor.FinalizeMigration();
    }

    public void FinalizeMigration()
    {
        _verkleStateTree.Commit();
        _verkleStateTree.CommitTree(0); // Assuming we're committing to block 0 for initial state
        Console.WriteLine($"Migration completed. Total accounts processed: {_accountsProcessed}");
    }
}
