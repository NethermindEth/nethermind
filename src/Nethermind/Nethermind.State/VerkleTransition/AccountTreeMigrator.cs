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

public class AccountTreeMigrator : ITreeVisitor
{
    private readonly VerkleStateTree _verkleStateTree;
    private readonly IStateReader _stateReader;
    private readonly IDb _preImageDb;
    private readonly List<byte[]> _currentPath = [];
    private Address? _lastAddress;
    private Account? _lastAccount;

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
        _currentPath.Clear();
        _lastAddress = null;
        _lastAccount = null;
    }

    public void VisitMissingNode(Hash256 nodeHash, TrieVisitContext trieVisitContext)
    {
        Console.WriteLine($"Warning: Missing node encountered: {nodeHash}");
    }

    public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
    {
        Console.WriteLine($"Visiting branch node: {trieVisitContext.AbsolutePathIndex.ToArray().ToHexString(false, true)} {trieVisitContext.BranchChildIndex}");
        if (trieVisitContext.BranchChildIndex.HasValue)
        {
            _currentPath.Add([(byte)trieVisitContext.BranchChildIndex.Value]);
        }
        else if (trieVisitContext.Level > 1 && _currentPath.Count > 0)
        {
            // Pop off last value when exiting branch. Since BranchChildIndex is only defined after each VisitBranch, we should not remove if
            // we are at level 1
            Console.WriteLine($"Exiting branch node, popping: {_currentPath[^1].ToHexString(false, true)}");
            _currentPath.RemoveAt(_currentPath.Count - 1);
        }
    }

    public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
    {
        if (node.Key is not null)
        {
            _currentPath.Add(Bytes.WithoutLeadingZeros(node.Key).ToArray());
        }
        else if (_currentPath.Count > 0)
        {
            // Pop off last value when exiting an extension node
            _currentPath.RemoveAt(_currentPath.Count - 1);
        }
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

    private byte[] ConstructFullHash(int branchIndex, byte[] nodeKey)
    {
        string branchPath = "";
        if (!_currentPath.IsNullOrEmpty())
        {
            branchPath = _currentPath.ToArray().CombineBytes().ToHexString(false, noLeadingZeros: true);
        }
        string branchIndexHex = branchIndex.ToString("x");
        string currentKey = ConvertPaddedHexToBytes(nodeKey.ToHexString()).ToHexString(false, noLeadingZeros: true);
        string addressHash = branchPath + branchIndexHex + currentKey;

        return Bytes.FromHexString(addressHash);
    }

    private byte[] ConstructFullHash(byte[] nodeKey)
    {
        string branchPath = "";
        if (!_currentPath.IsNullOrEmpty())
        {
            branchPath = _currentPath.ToArray().CombineBytes().ToHexString(false, noLeadingZeros: true);
        }
        string currentKey = ConvertPaddedHexToBytes(nodeKey.ToHexString()).ToHexString(false, noLeadingZeros: true);
        string addressHash = branchPath + currentKey;

        return Bytes.FromHexString(addressHash);

    }

    public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
    {
        if (!trieVisitContext.IsStorage)
        {
            Account account = decoder.Decode(new RlpStream(node.Value.ToArray()));

            // Reconstruct the full keccak hash
            byte[] addressHash;
            if (trieVisitContext.BranchChildIndex.HasValue)
            {
                addressHash = ConstructFullHash(trieVisitContext.BranchChildIndex.Value, node.Key);
            }
            else
            {
                addressHash = ConstructFullHash(node.Key);
            }
            byte[]? addressBytes = _preImageDb.Get(addressHash);
            if (addressBytes is not null)
            {
                var address = new Address(addressBytes);
                MigrateAccount(address, account);

                if (account.IsContract)
                {
                    MigrateContractCode(address, account.CodeHash);
                }

                _lastAddress = address;
                _lastAccount = account;
            }
        }
        else
        {
            if (_lastAddress is null || _lastAccount is null)
            {
                Console.WriteLine($"Address is null for storage node: {node}");
                return;
            }
            // Reconstruct the full keccak hash
            // byte[] storageHashFull;
            // if (trieVisitContext.BranchChildIndex.HasValue)
            // {
            //     storageHashFull = ConstructFullHash(trieVisitContext.BranchChildIndex.Value, node.Key);
            // }
            // else
            // {
            //     storageHashFull = ConstructFullHash(node.Key);
            // }

            byte[] storageValue = value.ToArray();
            MigrateAccountStorage(_lastAddress, trieVisitContext.AbsolutePathIndex.ToArray().ToUInt256(), storageValue);
        }
    }

    public void VisitCode(Hash256 codeHash, TrieVisitContext trieVisitContext) { }

    private void MigrateAccount(Address address, Account account)
    {
        _verkleStateTree.Set(address, account);
    }

    private void MigrateContractCode(Address address, Hash256 codeHash)
    {
        byte[]? code = _stateReader.GetCode(codeHash);
        if (code is not null)
        {
            _verkleStateTree.SetCode(address, code);
        }
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
