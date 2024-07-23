// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks.Dataflow;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.TreeStore;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.State;

public class VerkleStateTree(IVerkleTreeStore stateStore, ILogManager logManager) : VerkleTree(stateStore, logManager)
{
    [DebuggerStepThrough]
    public Account? Get(Address address, Hash256? stateRoot = null)
    {
        byte[] headerTreeKey = AccountHeader.GetTreeKeyPrefix(address.Bytes, 0);
        headerTreeKey[31] = AccountHeader.BasicDataLeafKey;
        byte[]? basicDataLeafVal = Get(headerTreeKey);
        if (basicDataLeafVal is null) return null;

        headerTreeKey[31] = AccountHeader.CodeHash;
        var codeHashBytes = Get(headerTreeKey);
        Hash256 codeHash = codeHashBytes is null ? Keccak.EmptyTreeHash : new Hash256(codeHashBytes);

        return AccountHeader.BasicDataToAccount(basicDataLeafVal, codeHash);
    }

    public bool TryGetStruct(Address address, out AccountStruct account, Hash256? rootHash = null)
    {
        byte[] headerTreeKey = AccountHeader.GetTreeKeyPrefix(address.Bytes, 0);
        headerTreeKey[31] = AccountHeader.BasicDataLeafKey;
        byte[]? basicDataLeafVal = Get(headerTreeKey);
        if (basicDataLeafVal is null)
        {
            account = AccountStruct.TotallyEmpty;
            return false;
        }

        headerTreeKey[31] = AccountHeader.CodeHash;
        var codeHashBytes = Get(headerTreeKey);
        ValueHash256 codeHash = codeHashBytes is null ? Keccak.EmptyTreeHash.ValueHash256 : new ValueHash256(codeHashBytes);

        account = AccountHeader.BasicDataToAccountStruct(basicDataLeafVal, codeHash);
        return true;
    }

    public void Set(Address address, Account? account)
    {
        byte[] headerTreeKey = AccountHeader.GetTreeKeyPrefix(address.Bytes, 0);
        if (account != null) InsertStemBatch(headerTreeKey.AsSpan()[..31], account.ToVerkleDict());
    }

    public byte[] Get(Address address, in UInt256 index, Hash256? stateRoot = null)
    {
        Hash256? key = AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, index);
        return (Get(key, stateRoot) ?? Array.Empty<byte>()).ToArray();
    }

    public void Set(Address address, in UInt256 index, byte[] value)
    {
        Hash256? key = AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, index);
        Insert(key, value);
    }

    public void SetCode(Address address, byte[] code)
    {
        UInt256 chunkId = 0;
        var codeEnumerator = new CodeChunkEnumerator(code);
        while (codeEnumerator.TryGetNextChunk(out byte[] chunk))
        {
            Hash256? key = AccountHeader.GetTreeKeyForCodeChunk(address.Bytes, chunkId);
            Insert(key, chunk);
            chunkId += 1;
        }
    }

    internal byte[] GetCode(Address address, Hash256? stateRoot = null)
    {
        using var codeStream = new MemoryStream();
        UInt256 chunkId = 0;
        while (true)
        {
            Hash256? key = AccountHeader.GetTreeKeyForCodeChunk(address.Bytes, chunkId);

            byte[] chunk = Get(key, stateRoot) ?? Array.Empty<byte>();

            // No more chunks
            if (chunk.Length == 0)
            {
                break;
            }

            codeStream.Write(chunk, 0, chunk.Length);
            chunkId += 1;
        }
        return codeStream.ToArray();
    }

    public void SetStorage(StorageCell cell, byte[] value)
    {
        Hash256? key = AccountHeader.GetTreeKeyForStorageSlot(cell.Address.Bytes, cell.Index);
        Insert(key, value);
    }

    public void BulkSet(IDictionary<StorageCell, byte[]> values)
    {
        // Put the sets into a list to be sorted
        using ArrayPoolList<KeyValuePair<StorageCell, byte[]>> theList = new ArrayPoolList<KeyValuePair<StorageCell, byte[]>>(values.Count, values);

        // Sort by address and index.
        theList.AsSpan().Sort((kv1, kv2) =>
        {
            int addressCompare = kv1.Key.Address.CompareTo(kv2.Key.Address);
            return addressCompare != 0 ? addressCompare : kv1.Key.Index.CompareTo(kv2.Key.Index);
        });

        ActionBlock<(Hash256, Dictionary<byte, byte[]>)> insertStem =
            new ActionBlock<(Hash256, Dictionary<byte, byte[]>)>((item) =>
            {
                InsertStemBatch(item.Item1.Bytes[..31], item.Item2.Select(kv => (kv.Key, kv.Value)));
            },
            new ExecutionDataflowBlockOptions()
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
            });

        Hash256 currentStem = Hash256.Zero;
        Dictionary<byte, byte[]> stemValues = new Dictionary<byte, byte[]>();
        foreach (KeyValuePair<StorageCell, byte[]> kv in theList)
        {
            // Because of the way the mapping works
            Hash256 theKey = AccountHeader.GetTreeKeyForStorageSlot(kv.Key.Address.Bytes, kv.Key.Index);

            if (!currentStem.Bytes[..31].SequenceEqual(theKey.Bytes[..31]))
            {
                // Different stem, will attempt to insert the stem batch
                if (stemValues.Count != 0)
                {
                    // Stem is different and stemValues have value.
                    insertStem.Post((currentStem, stemValues));
                    stemValues = new Dictionary<byte, byte[]>();
                }

                // And set the next stem
                currentStem = theKey;
            }

            stemValues[theKey.Bytes[31]] = kv.Value;
        }

        if (stemValues.Count != 0)
        {
            insertStem.Post((currentStem, stemValues));
        }

        insertStem.Complete();
        insertStem.Completion.Wait();
    }

    public static VerkleStateTree CreateStatelessTreeFromExecutionWitness(ExecutionWitness? execWitness, Banderwagon root, ILogManager logManager)
    {
        VerkleTreeStore<PersistEveryBlock>? stateStore = new(new MemColumnsDb<VerkleDbColumns>(), new MemDb(), logManager);
        VerkleStateTree? tree = new(stateStore, logManager);
        if (!tree.InsertIntoStatelessTree(execWitness, root))
        {
            throw new InvalidDataException("stateless tree cannot be created: invalid proof");
        }

        return tree;
    }
}
