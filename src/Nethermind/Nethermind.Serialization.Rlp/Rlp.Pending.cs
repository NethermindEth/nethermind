// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// TEMP-SWAP: members of Rlp that use decoders that are not ported yet. The step that ports a decoder moves its
// members back to Rlp.cs, Rlp.std.cs or Rlp.zkevm.cs. The last step deletes this file and RegisterPendingDecoders.

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Serialization.Rlp;

public partial class Rlp
{
    static partial void RegisterPendingDecoders()
    {
#if ZK_EVM
        RegisterDecoder(typeof(ReadOnlyAccountChanges), AccountChangesDecoder.Instance);
        RegisterDecoder(typeof(BalanceChange), BalanceChangeDecoder.Instance);
        RegisterDecoder(typeof(ReadOnlyBlockAccessList), BlockAccessListDecoder.Instance);
        RegisterDecoder(typeof(CodeChange), CodeChangeDecoder.Instance);
        RegisterDecoder(typeof(NonceChange), NonceChangeDecoder.Instance);
        RegisterDecoder(typeof(ReadOnlySlotChanges), SlotChangesDecoder.Instance);
        RegisterDecoder(typeof(StorageChange), StorageChangeDecoder.Instance);

        // Receipt decoders with explicit keys.
        RegisterDecoder(new RlpDecoderKey(typeof(TxReceipt), RlpDecoderKey.Default), new ReceiptMessageDecoder());
        RegisterDecoder(new RlpDecoderKey(typeof(TxReceipt), RlpDecoderKey.LegacyStorage), new ReceiptStorageDecoder());
        RegisterDecoder(new RlpDecoderKey(typeof(TxReceipt), RlpDecoderKey.Storage), CompactReceiptStorageDecoder.Instance);
        RegisterDecoder(new RlpDecoderKey(typeof(TxReceipt), RlpDecoderKey.Trie), new ReceiptMessageDecoder());
#endif
    }
}
