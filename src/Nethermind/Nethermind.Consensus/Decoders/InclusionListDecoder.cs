// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
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

    // Parallel infra has ~50μs startup; only worthwhile when the workload amortises that.
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

        using ArrayPoolList<Transaction?> slots = new(txBytes.Length, txBytes.Length);

        if (txBytes.Length >= ParallelThreshold)
        {
            ParallelUnbalancedWork.For(
                0,
                txBytes.Length,
                ParallelUnbalancedWork.DefaultOptions,
                (slots, txBytes, rlpDecoder, recoverer: _recoverSignatures, spec, logger: _logger),
                static (i, state) =>
                {
                    state.slots[i] = TryDecodeAndRecover(state.txBytes[i], state.rlpDecoder, state.recoverer, state.spec, state.logger);
                    return state;
                });
        }
        else
        {
            for (int i = 0; i < txBytes.Length; i++)
            {
                slots[i] = TryDecodeAndRecover(txBytes[i], rlpDecoder, _recoverSignatures, spec, _logger);
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
            // null-sender entries as not-appendable. Narrow catch keeps NRE/OOM observable.
            if (logger.IsTrace) logger.Trace($"IL signature recovery failed for tx {tx.Hash}: {e.GetType().Name}: {e.Message}");
        }

        return tx;
    }

    private static Transaction[] Compact(ArrayPoolList<Transaction?> slots)
    {
        int count = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] is not null) count++;
        }

        Transaction[] result = new Transaction[count];
        int j = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] is { } tx) result[j++] = tx;
        }
        return result;
    }

    public static byte[] Encode(Transaction transaction)
        => TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;

    public static byte[][] Encode(IEnumerable<Transaction> transactions)
        => [.. transactions.Select(Encode)];
}
