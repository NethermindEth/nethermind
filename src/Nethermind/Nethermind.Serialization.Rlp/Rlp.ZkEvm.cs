// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if ZK_EVM
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

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

    public static partial void RegisterDecoders(Assembly assembly,bool canOverrideExistingDecoders)
    {
        // Under ZK_EVM/bflat AOT we cannot rely on reflection-based auto-discovery of decoders
        // (CustomAttribute instantiation can trigger TypeLoader failures).
        // Register the required decoders explicitly instead.
        RegisterDecoder(typeof(Account), new AccountDecoder());
        RegisterDecoder(typeof(BlockBody), new BlockBodyDecoder());
        RegisterDecoder(typeof(Block), new BlockDecoder());
        RegisterDecoder(typeof(BlockInfo), new BlockInfoDecoder());
        RegisterDecoder(typeof(ChainLevelInfo), new ChainLevelDecoder());
        RegisterDecoder(typeof(LogEntry), new LogEntryDecoder());
        RegisterDecoder(typeof(Hash256), new KeccakDecoder());
        RegisterDecoder(typeof(BlockHeader), new HeaderDecoder());
        RegisterDecoder(typeof(Withdrawal), new WithdrawalDecoder());

        // Receipt decoders with explicit keys.
        RegisterDecoder(new RlpDecoderKey(typeof(TxReceipt), RlpDecoderKey.Storage), new CompactReceiptStorageDecoder());
        RegisterDecoder(new RlpDecoderKey(typeof(TxReceipt), RlpDecoderKey.LegacyStorage), new ReceiptArrayStorageDecoder());
        RegisterDecoder(new RlpDecoderKey(typeof(TxReceipt), RlpDecoderKey.Default), new ReceiptMessageDecoder());
        RegisterDecoder(new RlpDecoderKey(typeof(TxReceipt), RlpDecoderKey.Trie), new ReceiptMessageDecoder());

        RegisterDecoder(typeof(Transaction), TxDecoder.Instance);
    }
}

public readonly partial struct RlpDecoderKey
{
    public override int GetHashCode() => (int)BitOperations.Crc32C((uint)_type.GetHashCode(), (uint)MemoryMarshal.AsBytes(_key.AsSpan()).FastHash());
}
#endif
