// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Serialization.Rlp;

namespace Ethereum.Transaction.Test;

internal static class FrameTxPayload
{
    /// <summary>Encodes a frame transaction, type byte first, varying only the fields the caller supplies.</summary>
    /// <remarks>The defaults give no frames and no signatures; <paramref name="fees"/> replaces the whole fee list.</remarks>
    public static byte[] Encode(
        byte[] frames = null,
        byte[] maxFee = null,
        byte[][] nonceKeys = null,
        byte[] nonce = null,
        byte[] fees = null,
        byte[] maxPriorityFeePerGas = null)
    {
        byte[] payloadSequence = RlpTestHelper.EncodeSequence([
            Rlp.Encode(1L),                                 // chain_id
            .. nonceKeys is null ? [] : new[] { RlpTestHelper.EncodeSequence(nonceKeys) },
            nonce ?? Rlp.Encode(0L),                        // nonce, or nonce_seq after nonce_keys
            Rlp.Encode(TestItem.AddressA.Bytes),            // sender
            frames ?? RlpTestHelper.EncodeSequence(),
            RlpTestHelper.EncodeSequence(),                 // signatures
            fees ?? RlpTestHelper.EncodeSequence(                     // fees
                maxFee ?? Rlp.Encode(0L),
                maxPriorityFeePerGas ?? Rlp.Encode(0L)),
            RlpTestHelper.EncodeSequence()]);               // blob_versioned_hashes

        byte[] payload = new byte[1 + payloadSequence.Length];
        payload[0] = (byte)TxType.FrameTx;
        payloadSequence.CopyTo(payload, 1);
        return payload;
    }
}
