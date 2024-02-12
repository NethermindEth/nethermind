// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DotNetty.Common.Utilities;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    public AccountStruct? Get(Address address, Hash256? stateRoot = null)
    {
        Span<byte> key = AccountHeader.GetTreeKeyPrefix(address.Bytes, 0);

        key[31] = AccountHeader.Version;
        UInt256 version = new((Get(key, stateRoot) ?? Array.Empty<byte>()).ToArray());
        key[31] = AccountHeader.Balance;
        UInt256 balance = new((Get(key, stateRoot) ?? Array.Empty<byte>()).ToArray());
        key[31] = AccountHeader.Nonce;
        UInt256 nonce = new((Get(key, stateRoot) ?? Array.Empty<byte>()).ToArray());
        key[31] = AccountHeader.CodeHash;
        byte[]? codeHash = (Get(key, stateRoot) ?? Keccak.OfAnEmptyString.Bytes).ToArray();
        key[31] = AccountHeader.CodeSize;
        UInt256 codeSize = new((Get(key, stateRoot) ?? Array.Empty<byte>()).ToArray());

        return new AccountStruct(nonce, balance, codeSize, version, Keccak.EmptyTreeHash, new Hash256(codeHash));
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

    public void SetStorage(StorageCell cell, byte[] value)
    {
        Hash256? key = AccountHeader.GetTreeKeyForStorageSlot(cell.Address.Bytes, cell.Index);
        Insert(key, value);
    }

    public static VerkleStateTree CreateStatelessTreeFromExecutionWitness(ExecutionWitness? execWitness, Banderwagon root, ILogManager logManager)
    {
        VerkleTreeStore<PersistEveryBlock>? stateStore = new (new MemDb(), new MemDb(), new MemDb(), logManager);
        VerkleStateTree? tree = new (stateStore, logManager);
        if (!tree.InsertIntoStatelessTree(execWitness, root))
        {
            throw new InvalidDataException("stateless tree cannot be created: invalid proof");
        }

        return tree;
    }

}
