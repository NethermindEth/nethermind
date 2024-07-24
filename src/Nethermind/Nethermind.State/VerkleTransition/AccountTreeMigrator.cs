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
    private readonly List<byte[]> _currentPathStorage = [];
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
        List<byte[]> currentPath = trieVisitContext.IsStorage ? _currentPathStorage : _currentPath;
        // If there are more nodes traversed than the current level, we should pop the nodes until we are at the current level
        while (currentPath.Count >= trieVisitContext.Level && trieVisitContext.Level > 0)
        {
            currentPath.RemoveAt(currentPath.Count - 1);
        }
        if (trieVisitContext.BranchChildIndex.HasValue)
        {
            currentPath.Add(Bytes.FromHexString(trieVisitContext.BranchChildIndex.Value.ToString("x2")));
        }
    }

    public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
    {
        List<byte[]> currentPath = trieVisitContext.IsStorage ? _currentPathStorage : _currentPath;
        while (currentPath.Count >= trieVisitContext.Level && trieVisitContext.Level > 0)
        {
            currentPath.RemoveAt(currentPath.Count - 1);
        }
        else if (currentPath.Count > 0)
        if (trieVisitContext.BranchChildIndex.HasValue)
        {
            currentPath.Add(Bytes.FromHexString(trieVisitContext.BranchChildIndex.Value.ToString("x2")));
        }
        if (node.Key is not null)
        {
            currentPath.Add(node.Key);
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

    private static byte[] ConstructFullHash(byte[] nodeKey, List<byte[]> currentPath, int? branchIndex)
    {
        string branchPath = ConstructBranchPath(currentPath);
        string branchIndexHex = branchIndex is not null ? ((int)branchIndex).ToString("x2") : "";
        string currentKey = nodeKey.ToHexString();
        string addressHash = branchPath + branchIndexHex + currentKey;
        string unpaddedAddressHash = ConvertPaddedHexToBytes(addressHash).ToHexString();

        return Bytes.FromHexString(unpaddedAddressHash);
    }

    private static string ConstructBranchPath(List<byte[]> currentPath)
    {
        if (currentPath.IsNullOrEmpty())
        {
            return "";
        }
        return currentPath.ToArray().CombineBytes().ToHexString();
    }

    public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
    {
        List<byte[]> currentPath = trieVisitContext.IsStorage ? _currentPathStorage : _currentPath;

        while (currentPath.Count >= trieVisitContext.Level && trieVisitContext.Level > 0)
        {
            currentPath.RemoveAt(currentPath.Count - 1);
        }

        if (!trieVisitContext.IsStorage)
        {
            Account account = decoder.Decode(new RlpStream(node.Value.ToArray()));

            // Reconstruct the full keccak hash
            byte[] addressHash = ConstructFullHash(node.Key, _currentPath, trieVisitContext.BranchChildIndex);
            byte[]? addressBytes = _preImageDb.Get(addressHash);
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
            byte[] storageSlotHash = ConstructFullHash(node.Key, _currentPathStorage, trieVisitContext.BranchChildIndex);

            byte[]? storageSlotBytes = _preImageDb.Get(storageSlotHash);
            if (storageSlotBytes is null)
            {
                Console.WriteLine($"Storage slot is null for node: {node} with key: {storageSlotHash.ToHexString()}");
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


    public void VisitCode(Hash256 codeHash, TrieVisitContext trieVisitContext) { }

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
