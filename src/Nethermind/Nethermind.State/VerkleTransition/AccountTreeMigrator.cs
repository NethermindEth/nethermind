// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Int256;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using System.Linq;

namespace Nethermind.State.VerkleTransition;

public class AccountTreeMigrator : ITreeVisitor
{
    private readonly VerkleStateTree _verkleStateTree;
    private readonly IStateReader _stateReader;
    private readonly IDb _preImageDb;

    public AccountTreeMigrator(VerkleStateTree verkleStateTree, IStateReader stateReader, IDb preImageDb)
    {
        _verkleStateTree = verkleStateTree;
        _stateReader = stateReader;
        _preImageDb = preImageDb;
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

    /// <summary>
    /// Get the address from the node key. Currently, the last 20 bytes of the key are used as the address.
    /// TODO: remove this once we have implemented pre-image db logic
    /// </summary>
    /// <param name="nodeKey"></param>
    /// <returns></returns>
    private Address? GetAddress(byte[] nodeKey)
    {
        byte[] unpaddedNodeKey = ConvertPaddedHexToBytes(nodeKey.ToHexString());
        Console.WriteLine($"Unpadded node key: {unpaddedNodeKey.ToHexString()}");
        return new Address(unpaddedNodeKey.Slice(unpaddedNodeKey.Length - 20, 20));
    }


    private static byte[] ConvertPaddedHexToBytes(string paddedHex)
    {
        // Remove the padding (leading zeros)
        string unpadded = string.Concat(
            paddedHex
                .Where((c, i) => i % 2 == 1)
                .Select(c => c.ToString())
        );

        // Convert the unpadded hex string to byte array
        return Enumerable.Range(0, unpadded.Length / 2)
            .Select(x => Convert.ToByte(unpadded.Substring(x * 2, 2), 16))
            .ToArray();
    }


    public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
    {
        Console.WriteLine($"Visiting leaf for node: {node}");

        if (!trieVisitContext.IsStorage)
        {
            Account account = decoder.Decode(new RlpStream(node.Value.ToArray()));

            Address? address = GetAddress(node.Key);
            if (address is null)
            {
                Console.WriteLine($"Address is null for node: {node}");
                return;
            }
            Console.WriteLine($"Migrating account for {address} {node.Key.ToHexString()}");

            MigrateAccount(address, account);

            if (account.IsContract)
            {
                MigrateContractCode(address, account.CodeHash);
            }
        }
        else
        {
            Address? address = GetAddress(node.Key);
            if (address is null)
            {
                Console.WriteLine($"Address is null for storage node: {node}");
                return;
            }
            Console.WriteLine($"Migrating storage for {address} {node.Key.ToHexString()}");

            byte[] storageValue = value.ToArray();
            MigrateAccountStorage(address, node.Key.ToUInt256(), storageValue);
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
        Console.WriteLine($"Migrating code for {address} {codeHash.Bytes.ToHexString()}");
        _verkleStateTree.SetCode(address, code);
    }

    private void MigrateAccountStorage(Address address, UInt256 index, byte[] value)
    {
        StorageCell storageKey = new StorageCell(address, index);
        _verkleStateTree.SetStorage(storageKey, value);
    }

    public void FinalizeMigration(long blockNumber)
    {
        _verkleStateTree.Commit();
        _verkleStateTree.CommitTree(blockNumber);
        Console.WriteLine($"Migration completed");
    }
}
