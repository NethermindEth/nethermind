// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.Arkiv.Execution;

/// <summary>
/// The protocol transaction every Arkiv block starts with: <c>purgeExpired(bytes32[])</c> to <c>0x44…44</c>, from the
/// protocol sender, its nonce the block number — exactly as <c>arkiv-reth-payload-builder</c>'s <c>protocol_transaction</c>
/// signs it, and as the executor's <c>is_protocol_purge</c> requires (type 2, gas limit 1 000 000, max fee 2^128 − 1,
/// no tip, no value, no access list).
/// </summary>
public static class ArkivPurge
{
    public static readonly Address Arkiv = new("0x4400000000000000000000000000000000000044");
    public static readonly Address Sender = new("0x5ff20d1c9fea02c505a2c18f6d42feaeaf44c0af");
    public const ulong GasLimit = 1_000_000;

    /// <summary>The envelope key arkiv-reth publishes: "public protocol material, not an authentication secret".</summary>
    private static readonly PrivateKey Key = new("8b3a350cf5c34c9194ca3a545d4b54b69356a5f5a39d9c7f94a17e5f7f9a6c31");

    /// <summary><c>keccak("purgeExpired(bytes32[])")[..4]</c>.</summary>
    private static readonly byte[] Selector = Bytes.FromHexString("0xad9e3f1c");

    public static Transaction Create(ulong chainId, ulong blockNumber, IReadOnlyList<Hash256> keys)
    {
        Transaction tx = new()
        {
            Type = TxType.EIP1559,
            ChainId = chainId,
            Nonce = blockNumber,
            GasLimit = GasLimit,
            DecodedMaxFeePerGas = (UInt256.One << 128) - 1,
            GasPrice = UInt256.Zero,
            To = Arkiv,
            Value = UInt256.Zero,
            Data = Calldata(keys),
        };
        new EthereumEcdsa(chainId).Sign(Key, tx);
        tx.SenderAddress = Sender;
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    /// <summary>The ABI encoding of <c>purgeExpired(bytes32[] keys)</c>: selector, offset 0x20, length, the keys.</summary>
    public static byte[] Calldata(IReadOnlyList<Hash256> keys)
    {
        byte[] data = new byte[4 + 32 + 32 + 32 * keys.Count];
        Selector.CopyTo(data, 0);
        data[4 + 31] = 0x20;
        new UInt256((ulong)keys.Count).ToBigEndian(data.AsSpan(36, 32));
        for (int i = 0; i < keys.Count; i++) keys[i].Bytes.CopyTo(data.AsSpan(68 + 32 * i, 32));
        return data;
    }
}
