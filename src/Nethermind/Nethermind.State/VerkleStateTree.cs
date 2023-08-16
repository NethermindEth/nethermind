// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Interfaces;

namespace Nethermind.State;

public class VerkleStateTree : VerkleTree
{

    public VerkleStateTree(IDbProvider dbProvider, ILogManager logManager) : base(dbProvider, logManager) { }

    public VerkleStateTree(IVerkleTrieStore stateStore, ILogManager logManager) : base(stateStore, logManager) { }

    [DebuggerStepThrough]
    public Account? Get(Address address, Keccak? rootHash = null)
    {
        Span<byte> key = new byte[32];
        Pedersen headerTreeKey = AccountHeader.GetTreeKeyPrefixAccount(address.Bytes);
        headerTreeKey.StemAsSpan.CopyTo(key);
        key[31] = AccountHeader.Version;
        UInt256 version = new((Get(new Pedersen(key.ToArray())) ?? Array.Empty<byte>()).ToArray());
        key[31] = AccountHeader.Balance;
        UInt256 balance = new((Get(new Pedersen(key.ToArray())) ?? Array.Empty<byte>()).ToArray());
        key[31] = AccountHeader.Nonce;
        UInt256 nonce = new((Get(new Pedersen(key.ToArray())) ?? Array.Empty<byte>()).ToArray());
        key[31] = AccountHeader.CodeHash;
        byte[]? codeHash = (Get(new Pedersen(key.ToArray())) ?? Keccak.OfAnEmptyString.Bytes).ToArray();
        key[31] = AccountHeader.CodeSize;
        UInt256 codeSize = new((Get(new Pedersen(key.ToArray())) ?? Array.Empty<byte>()).ToArray());

        return new Account(balance, nonce, codeSize, version, Keccak.EmptyTreeHash, new Keccak(codeHash));
    }

    public void Set(Address address, Account? account)
    {
        Pedersen headerTreeKey = AccountHeader.GetTreeKeyPrefixAccount(address.Bytes);
        if (account != null) InsertStemBatch(headerTreeKey.StemAsSpan, account.ToVerkleDict());
    }

    public byte[] Get(Address address, in UInt256 index, Keccak? storageRoot = null)
    {
        Pedersen key = AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, index);
        return (Get(key) ?? Array.Empty<byte>()).ToArray();
    }

    public void Set(Address address, in UInt256 index, byte[] value)
    {
        Pedersen key = AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, index);
        Insert(key, value);
    }

    public void SetCode(Address address, byte[] code)
    {
        UInt256 chunkId = 0;
        CodeChunkEnumerator codeEnumerator = new CodeChunkEnumerator(code);
        while (codeEnumerator.TryGetNextChunk(out byte[] chunk))
        {
            Pedersen key = AccountHeader.GetTreeKeyForCodeChunk(address.Bytes, chunkId);
            Insert(key, chunk);
            chunkId += 1;
        }
    }

    public void SetStorage(StorageCell cell, byte[] value)
    {
        Pedersen key = AccountHeader.GetTreeKeyForStorageSlot(cell.Address.Bytes, cell.Index);
        Insert(key, value);
    }

    public static VerkleStateTree CreateStatelessTreeFromExecutionWitness(ExecutionWitness? execWitness, Banderwagon root, ILogManager logManager)
    {
        VerkleStateStore? stateStore = new (new MemDb(), new MemDb(), new MemDb(), logManager);
        VerkleStateTree? tree = new (stateStore, logManager);
        if (!tree.InsertIntoStatelessTree(execWitness, root))
        {
            throw new InvalidDataException("stateless tree cannot be created: invalid proof");
        }

        return tree;
    }

}
