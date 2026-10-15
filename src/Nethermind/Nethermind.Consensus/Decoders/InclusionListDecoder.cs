// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Decoders;

/// <summary>Converts between an inclusion list's engine-API form and its transactions (EIP-7805).</summary>
/// <remarks>Entries carry the EIP-2718 <c>TransactionType || TransactionPayload</c> form, so encoding skips
/// the typed RLP wrapper.</remarks>
public class InclusionListDecoder(
    IEthereumEcdsa? ecdsa,
    ISpecProvider? specProvider,
    ILogManager? logManager)
{
    private readonly RecoverSignatures _recoverSignatures = new(ecdsa, specProvider, logManager);

    /// <summary>Decodes the list and recovers its senders, tolerating entries that are neither.</summary>
    /// <remarks>
    /// The list is untrusted consensus-client input, so a bad entry must not fail the call: undecodable
    /// entries are dropped and an unrecoverable sender is left null, which appendability reads as
    /// not-appendable. The returned array can therefore be shorter than <paramref name="txBytes"/>.
    /// </remarks>
    public Transaction[] DecodeAndRecover(byte[][] txBytes, IReleaseSpec spec)
    {
        Transaction[] txs = TxsDecoder.DecodeTxs(txBytes, skipErrors: true).Transactions;
        _recoverSignatures.RecoverData(txs, spec, skipErrors: true);
        return txs;
    }

    /// <summary>Decodes each committee position's list and recovers the senders, tolerating entries that are neither.</summary>
    /// <remarks>As <see cref="DecodeAndRecover(byte[][], IReleaseSpec)"/>, list by list, so each decoded list can be
    /// shorter than its input. Envelopes repeated across positions share one decoded transaction, whose sender is
    /// recovered and whose signatures are verified once however many lists carry it.</remarks>
    public Transaction[][] DecodeAndRecover(byte[][][] inclusionLists, IReleaseSpec spec)
    {
        Transaction[][] decoded = DecodeLists(inclusionLists);
        _recoverSignatures.RecoverData(InclusionLists.Distinct(decoded), spec, skipErrors: true);
        return decoded;
    }

    /// <summary>Decodes each committee position's list, dropping undecodable entries and sharing one transaction
    /// per hash across positions.</summary>
    public static Transaction[][] DecodeLists(byte[][][] inclusionLists)
    {
        Dictionary<Hash256AsKey, Transaction> byHash = [];
        Transaction[][] decoded = new Transaction[inclusionLists.Length][];
        for (int position = 0; position < inclusionLists.Length; position++)
        {
            Transaction[] transactions = TxsDecoder.DecodeTxs(inclusionLists[position] ?? [], skipErrors: true).Transactions;
            for (int i = 0; i < transactions.Length; i++)
            {
                if (transactions[i].Hash is not { } hash) continue;
                if (byHash.TryGetValue(hash, out Transaction? shared)) transactions[i] = shared;
                else byHash[hash] = transactions[i];
            }
            decoded[position] = transactions;
        }
        return decoded;
    }

    private static byte[] Encode(Transaction transaction)
    {
        TxDecoder decoder = TxDecoder.Instance;
        byte[] buffer = new byte[decoder.GetLength(transaction, RlpBehaviors.SkipTypedWrapping)];
        RlpWriter writer = new(buffer);
        decoder.Encode(ref writer, transaction, RlpBehaviors.SkipTypedWrapping);
        return buffer;
    }

    /// <summary>Encodes one entry into a pooled buffer the caller owns and must dispose.</summary>
    public static ArrayPoolList<byte> EncodePooled(Transaction transaction)
    {
        TxDecoder decoder = TxDecoder.Instance;
        int length = decoder.GetLength(transaction, RlpBehaviors.SkipTypedWrapping);
        ArrayPoolList<byte> result = new(length, length);
        RlpWriter writer = new(result.AsSpan());
        decoder.Encode(ref writer, transaction, RlpBehaviors.SkipTypedWrapping);
        return result;
    }

    /// <summary>Encodes a decoded list back into the entries the engine API carries.</summary>
    public static byte[][] Encode(Transaction[] transactions)
    {
        byte[][] result = new byte[transactions.Length][];
        for (int i = 0; i < transactions.Length; i++)
        {
            result[i] = Encode(transactions[i]);
        }
        return result;
    }
}
