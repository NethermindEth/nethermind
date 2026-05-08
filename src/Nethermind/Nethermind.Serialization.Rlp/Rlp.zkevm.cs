// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Serialization.Rlp;

public partial class Rlp
{
    private static Dictionary<RlpDecoderKey, IRlpDecoder>? _decodersSnapshot;

    public static Dictionary<RlpDecoderKey, IRlpDecoder> Decoders
    {
        get
        {
            Dictionary<RlpDecoderKey, IRlpDecoder>? snapshot = _decodersSnapshot;
            return snapshot ?? CreateDecodersSnapshot();
        }
    }

    private static Dictionary<RlpDecoderKey, IRlpDecoder> CreateDecodersSnapshot()
    {
        using Lock.Scope _ = _decoderLock.EnterScope();
        return _decodersSnapshot ??= new Dictionary<RlpDecoderKey, IRlpDecoder>(_decoderBuilder);
    }

    public static partial void RegisterDecoders(Assembly assembly, bool canOverrideExistingDecoders)
    {
        // Under zkEVM/bflat AOT we cannot rely on reflection-based auto-discovery of decoders
        // (CustomAttribute instantiation can trigger TypeLoader failures).
        // Register the required decoders explicitly instead.
        RegisterDecoder(typeof(Account), AccountDecoder.Instance);
        RegisterDecoder(typeof(AccountChanges), AccountChangesDecoder.Instance);
        RegisterDecoder(typeof(AuthorizationTuple), new AuthorizationTupleDecoder());
        RegisterDecoder(typeof(BalanceChange), BalanceChangeDecoder.Instance);
        RegisterDecoder(typeof(Block), new BlockDecoder());
        RegisterDecoder(typeof(BlockAccessList), BlockAccessListDecoder.Instance);
        RegisterDecoder(typeof(BlockBody), BlockBodyDecoder.Instance);
        RegisterDecoder(typeof(BlockHeader), new HeaderDecoder());
        RegisterDecoder(typeof(BlockInfo), BlockInfoDecoder.Instance);
        RegisterDecoder(typeof(ChainLevelInfo), new ChainLevelDecoder());
        RegisterDecoder(typeof(CodeChange), CodeChangeDecoder.Instance);
        RegisterDecoder(typeof(Hash256), KeccakDecoder.Instance);
        RegisterDecoder(typeof(LogEntry), LogEntryDecoder.Instance);
        RegisterDecoder(typeof(NonceChange), NonceChangeDecoder.Instance);
        RegisterDecoder(typeof(SlotChanges), SlotChangesDecoder.Instance);
        RegisterDecoder(typeof(StorageChange), StorageChangeDecoder.Instance);
        RegisterDecoder(typeof(Transaction), TxDecoder.Instance);
        RegisterDecoder(typeof(Withdrawal), new WithdrawalDecoder());

        // Receipt decoders with explicit keys.
        RegisterDecoder(new RlpDecoderKey(typeof(TxReceipt), RlpDecoderKey.Default), new ReceiptMessageDecoder());
        RegisterDecoder(new RlpDecoderKey(typeof(TxReceipt), RlpDecoderKey.LegacyStorage), ReceiptArrayStorageDecoder.Instance);
        RegisterDecoder(new RlpDecoderKey(typeof(TxReceipt), RlpDecoderKey.Storage), CompactReceiptStorageDecoder.Instance);
        RegisterDecoder(new RlpDecoderKey(typeof(TxReceipt), RlpDecoderKey.Trie), new ReceiptMessageDecoder());
    }
}

public readonly partial struct RlpDecoderKey
{
    public override int GetHashCode() => (int)BitOperations.Crc32C((uint)_type.GetHashCode(), (uint)MemoryMarshal.AsBytes(_key.AsSpan()).FastHash());
}
