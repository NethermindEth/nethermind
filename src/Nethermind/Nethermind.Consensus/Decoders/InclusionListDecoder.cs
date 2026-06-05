// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Decoders;

/// <summary>
/// EIP-7805 (FOCIL) IL transaction decoder: RLP-decodes the CL's byte blobs and recovers
/// each tx's sender + EIP-7702 authority addresses in a single parallel pass. Unparsable
/// entries are dropped per the spec's "no-op on bad item" rule.
/// </summary>
public class InclusionListDecoder(
    IEthereumEcdsa? ecdsa,
    ISpecProvider? specProvider,
    ILogManager? logManager)
{
    private readonly RecoverSignatures _recoverSignatures = new(ecdsa, specProvider, logManager);
    private readonly ILogger _logger = (logManager ?? NullLogManager.Instance).GetClassLogger<InclusionListDecoder>();
    private const int ParallelThreshold = 16;

    public Transaction[] DecodeAndRecover(byte[][] txBytes, IReleaseSpec spec)
    {
        if (txBytes.Length == 0)
        {
            return [];
        }

        IRlpDecoder<Transaction>? rlpDecoder = Rlp.GetDecoder<Transaction>();
        if (rlpDecoder is null)
        {
            return [];
        }

        // Declared non-nullable but null slots are legal at runtime (nullability is a compile-time
        // annotation; CLR T[] = T?[]). Failed decodes leave null until Compact removes them.
        Transaction[] slots = new Transaction[txBytes.Length];

        if (txBytes.Length >= ParallelThreshold)
        {
            ParallelUnbalancedWork.For(
                0,
                txBytes.Length,
                ParallelUnbalancedWork.DefaultOptions,
                (slots, txBytes, rlpDecoder, recoverer: _recoverSignatures, spec, logger: _logger),
                static (i, state) =>
                {
                    state.slots[i] = TryDecodeAndRecover(state.txBytes[i], state.rlpDecoder, state.recoverer, state.spec, state.logger)!;
                    return state;
                });
        }
        else
        {
            for (int i = 0; i < txBytes.Length; i++)
            {
                slots[i] = TryDecodeAndRecover(txBytes[i], rlpDecoder, _recoverSignatures, spec, _logger)!;
            }
        }

        return Compact(slots);
    }

    private static Transaction? TryDecodeAndRecover(
        byte[] bytes,
        IRlpDecoder<Transaction> rlpDecoder,
        RecoverSignatures recoverer,
        IReleaseSpec spec,
        ILogger logger)
    {
        Transaction tx;
        try
        {
            Rlp.ValueDecoderContext ctx = new(bytes);
            tx = rlpDecoder.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
        }
        catch (Exception e) when (e is RlpException or ArgumentException or IndexOutOfRangeException)
        {
            // Spec: unparsable IL items are a no-op rather than a protocol error.
            return null;
        }

        try
        {
            recoverer.RecoverOne(tx, spec);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            // Recovery failed → keep the tx with null SenderAddress; the validator handles
            // null-sender entries as not-appendable.
            if (logger.IsTrace) logger.Trace($"IL signature recovery failed for tx {tx.Hash}: {e.GetType().Name}: {e.Message}");
        }

        return tx;
    }

    private static Transaction[] Compact(Transaction[] slots)
    {
        int j = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] is not null)
            {
                if (i != j) slots[j] = slots[i];
                j++;
            }
        }
        if (j == slots.Length) return slots;
        Array.Resize(ref slots, j);
        return slots;
    }

    public static byte[] Encode(Transaction transaction)
        => TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;

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
