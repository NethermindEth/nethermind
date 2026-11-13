// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;

namespace Ethereum.Transaction.Test;

internal static class FrameTxPayload
{
    /// <summary>Encodes a frame transaction, type byte first, varying only the fields the caller supplies.</summary>
    /// <remarks>The defaults give no frames and no signatures; <paramref name="fees"/> replaces the whole fee list.</remarks>
    public static byte[] Encode(
        Rlp frames = null,
        Rlp maxFee = null,
        Rlp[] nonceKeys = null,
        Rlp nonce = null,
        Rlp fees = null,
        Rlp maxPriorityFeePerGas = null)
    {
        Rlp payloadSequence = Rlp.Encode([
            Rlp.Encode(1L),                                 // chain_id
            .. nonceKeys is null ? [] : new[] { Rlp.Encode(nonceKeys) },
            nonce ?? Rlp.Encode(0L),                        // nonce, or nonce_seq after nonce_keys
            Rlp.Encode(TestItem.AddressA.Bytes),            // sender
            frames ?? Rlp.Encode(Array.Empty<Rlp>()),
            Rlp.Encode(Array.Empty<Rlp>()),                 // signatures
            fees ?? Rlp.Encode(                             // fees
                maxFee ?? Rlp.Encode(0L),
                maxPriorityFeePerGas ?? Rlp.Encode(0L)),
            Rlp.Encode(Array.Empty<Rlp>())]);               // blob_versioned_hashes

        byte[] payload = new byte[1 + payloadSequence.Length];
        payload[0] = (byte)TxType.FrameTx;
        payloadSequence.Bytes.CopyTo(payload, 1);
        return payload;
    }
}
