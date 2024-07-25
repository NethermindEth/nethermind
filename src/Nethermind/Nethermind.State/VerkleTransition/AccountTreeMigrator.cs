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
using System.Collections.Generic;
using Microsoft.IdentityModel.Tokens;
using DotNetty.Common.Utilities;

namespace Nethermind.State.VerkleTransition;

public class AccountTreeMigrator : ITreeVisitor<TreePathContext>
{
    private readonly VerkleStateTree _verkleStateTree;
    private readonly IStateReader _stateReader;
    private readonly IDb _preImageDb;
    private Address? _lastAddress;
    private Account? _lastAccount;
    private int _leafNodeCounter = 0;

    private const int StateTreeCommitThreshold = 1000;

    public AccountTreeMigrator(VerkleStateTree verkleStateTree, IStateReader stateReader, IDb preImageDb)
    {
        _verkleStateTree = verkleStateTree;
        _stateReader = stateReader;
        _preImageDb = preImageDb;
    }

    public bool IsFullDbScan => true;

    public bool ShouldVisit(in TreePathContext ctx, Hash256 nextNode)
    {
        return true;
    }

    public void VisitTree(in TreePathContext nodeContext, Hash256 rootHash, TrieVisitContext trieVisitContext)
    {
        Console.WriteLine($"Starting migration from Merkle tree with root: {rootHash}");
        _lastAddress = null;
        _lastAccount = null;
    }

    public void VisitMissingNode(in TreePathContext nodeContext, Hash256 nodeHash, TrieVisitContext trieVisitContext)
    {
        Console.WriteLine($"Warning: Missing node encountered: {nodeHash}");
    }

    public void VisitBranch(in TreePathContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitExtension(in TreePathContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext)
    {
    }

    private readonly AccountDecoder decoder = new();

    /// <summary>
    /// Converts a 0-padded hex string to a byte array, i.e. from '050502' to '552'.
    /// TODO: check if this is the best way to do this
    /// </summary>
    /// <param name="paddedHex"></param>
    /// <returns></returns>
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

    public void VisitLeaf(in TreePathContext nodeContext, TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
    {
        TreePath path = nodeContext.Path.Append(node.Key);

        if (!trieVisitContext.IsStorage)
        {
            Account account = decoder.Decode(new RlpStream(node.Value.ToArray()));

            // Reconstruct the full keccak hash
            byte[]? addressBytes = _preImageDb.Get(path.Path.BytesAsSpan);
            if (addressBytes is not null)
            {
                var address = new Address(addressBytes);

                // Update code size if account has code
                if (account.HasCode)
                {
                    byte[]? code = _stateReader.GetCode(account.CodeHash);
                    if (code is not null)
                    {
                        account.CodeSize = (UInt256)code.Length;
                        MigrateContractCode(address, code);
                    }
                }
                MigrateAccount(address, account);

                _lastAddress = address;
                _lastAccount = account;
            }
        }
        else
        {
            if (_lastAddress is null || _lastAccount is null)
            {
                Console.WriteLine($"No address or account detected for storage node: {node}");
                return;
            }
            // Reconstruct the full keccak hash
            byte[]? storageSlotBytes = _preImageDb.Get(path.Path.BytesAsSpan);
            if (storageSlotBytes is null)
            {
                Console.WriteLine($"Storage slot is null for node: {node} with key: {path.Path.BytesAsSpan.ToHexString()}");
                return;
            }
            UInt256 storageSlot = new(storageSlotBytes);
            byte[] storageValue = value.ToArray();
            MigrateAccountStorage(_lastAddress, storageSlot, storageValue);
        }

        CommitIfThresholdReached();
    }

    private void CommitIfThresholdReached()
    {
        _leafNodeCounter++;
        if (_leafNodeCounter >= StateTreeCommitThreshold)
        {
            _verkleStateTree.Commit();
            _leafNodeCounter = 0;
        }
    }


    public void VisitCode(in TreePathContext nodeContext, Hash256 codeHash, TrieVisitContext trieVisitContext) { }

    private void MigrateAccount(Address address, Account account)
    {
        _verkleStateTree.Set(address, account);
    }

    private void MigrateContractCode(Address address, byte[] code)
    {
        _verkleStateTree.SetCode(address, code);
    }

    private void MigrateAccountStorage(Address address, UInt256 index, byte[] value)
    {
        var storageKey = new StorageCell(address, index);
        _verkleStateTree.SetStorage(storageKey, value);
    }

    public void FinalizeMigration(long blockNumber)
    {
        // Commit any remaining changes
        if (_leafNodeCounter > 0)
        {
            _verkleStateTree.Commit();
        }
        _verkleStateTree.CommitTree(blockNumber);
        Console.WriteLine($"Migration completed");
    }
}
