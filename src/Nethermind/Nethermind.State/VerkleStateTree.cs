// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Tree;

namespace Nethermind.State;

public class VerkleStateTree: VerkleTree
{

    public VerkleStateTree(IDbProvider dbProvider) : base(dbProvider)
    {
    }

    public VerkleStateTree(IVerkleStore stateStore) : base(stateStore)
    {
    }

    [DebuggerStepThrough]
    public Account? Get(Address address, Keccak? rootHash = null)
    {
        Span<byte> key = new byte[32];
        byte[]? headerTreeKey = AccountHeader.GetTreeKeyPrefixAccount(address.Bytes);
        headerTreeKey.CopyTo(key);
        key[31] = AccountHeader.Version;
        UInt256 version = new UInt256((Get(key.ToArray()) ?? Array.Empty<byte>()).ToArray());
        key[31] = AccountHeader.Balance;
        UInt256 balance = new UInt256((Get(key.ToArray()) ?? Array.Empty<byte>()).ToArray());
        key[31] = AccountHeader.Nonce;
        UInt256 nonce = new UInt256((Get(key.ToArray()) ?? Array.Empty<byte>()).ToArray());
        key[31] = AccountHeader.CodeHash;
        byte[]? codeHash = (Get(key.ToArray()) ?? Keccak.OfAnEmptyString.Bytes).ToArray();
        key[31] = AccountHeader.CodeSize;
        UInt256 codeSize = new UInt256((Get(key.ToArray()) ?? Array.Empty<byte>()).ToArray());

        return new Account(balance, nonce, new Keccak(codeHash), codeSize, version);
    }

    public void Set(Address address, Account? account)
    {
        byte[]? headerTreeKey = AccountHeader.GetTreeKeyPrefixAccount(address.Bytes);
        if (account != null) InsertStemBatch(headerTreeKey[..31], account.ToVerkleDict());
    }

    public byte[] Get(Address address, in UInt256 index, Keccak? storageRoot = null)
    {
        byte[]? key = AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, index).ToArray();
        return (Get(key) ?? Array.Empty<byte>()).ToArray();
    }

    public void Set(Address address, in UInt256 index, byte[] value)
    {
        byte[] key = AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, index).ToArray();
        Insert(key, value);
    }
}
