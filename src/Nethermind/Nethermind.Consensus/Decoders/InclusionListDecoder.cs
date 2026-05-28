// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Decoders;

/// <summary>
/// EIP-7805 (FOCIL) IL transaction decoder. Decodes the byte-blob list supplied by the CL
/// via PayloadAttributesV5 and recovers each tx's sender + EIP-7702 authority addresses.
/// </summary>
/// <remarks>
/// Decode and ECDSA recovery run in a single parallel pass (above a small threshold) so the
/// FCUv5 hot path pays one Parallel scheduling cost instead of two — the previous pipeline
/// invoked <see cref="TxsDecoder.DecodeTxs"/> and <see cref="RecoverSignatures.RecoverData"/>
/// back-to-back as separate parallel loops. Bad entries (garbage RLP, empty arrays, bounds
/// failures) are dropped silently per the spec's "unparsable IL items are a no-op" rule.
/// </remarks>
public class InclusionListDecoder(
    IEthereumEcdsa? ecdsa,
    ISpecProvider? specProvider,
    Logging.ILogManager? logManager)
{
    private readonly RecoverSignatures _recoverSignatures = new(ecdsa, specProvider, logManager);

    // ILs are bounded by Eip7805Constants.MaxTransactionsPerInclusionList (16), so the
    // parallel threshold is set low. Above 3 the Parallel.For dispatch cost is comfortably
    // amortised by RLP decode + ECDSA recovery work per tx; below it the serial path avoids
    // touching the thread pool entirely.
    private const int ParallelThreshold = 4;

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

        Transaction?[] slots = new Transaction?[txBytes.Length];

        if (txBytes.Length >= ParallelThreshold)
        {
            ParallelUnbalancedWork.For(
                0,
                txBytes.Length,
                ParallelUnbalancedWork.DefaultOptions,
                (slots, txBytes, rlpDecoder, recoverer: _recoverSignatures, spec),
                static (i, state) =>
                {
                    state.slots[i] = TryDecodeAndRecover(state.txBytes[i], state.rlpDecoder, state.recoverer, state.spec);
                    return state;
                });
        }
        else
        {
            for (int i = 0; i < txBytes.Length; i++)
            {
                slots[i] = TryDecodeAndRecover(txBytes[i], rlpDecoder, _recoverSignatures, spec);
            }
        }

        return CompactSlots(slots);
    }

    private static Transaction? TryDecodeAndRecover(
        byte[] bytes,
        IRlpDecoder<Transaction> rlpDecoder,
        RecoverSignatures recoverer,
        IReleaseSpec spec)
    {
        Transaction tx;
        try
        {
            Rlp.ValueDecoderContext ctx = new(bytes);
            tx = rlpDecoder.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
        }
        catch (Exception e) when (e is RlpException or ArgumentException or IndexOutOfRangeException)
        {
            // Spec: unparsable IL items are a no-op rather than a protocol error. Bounds /
            // RLP / argument failures all collapse to "drop the entry" so a single malformed
            // byte string can't poison the whole IL.
            return null;
        }

        try
        {
            recoverer.RecoverOne(tx, spec);
        }
        catch (Exception)
        {
            // Keep the partially-decoded tx even if signature recovery fails. The validator
            // already guards on `tx.SenderAddress is null` and treats those entries as
            // not-appendable, so the IL satisfaction check stays correct without us having
            // to drop the entry here.
        }

        return tx;
    }

    private static Transaction[] CompactSlots(Transaction?[] slots)
    {
        int count = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] is not null) count++;
        }

        // Fast path: no failed decodes, so the slots array is already dense — skip the
        // compaction pass and just cast each slot.
        if (count == slots.Length)
        {
            Transaction[] dense = new Transaction[slots.Length];
            for (int i = 0; i < slots.Length; i++) dense[i] = slots[i]!;
            return dense;
        }

        Transaction[] result = new Transaction[count];
        int j = 0;
        for (int i = 0; i < slots.Length; i++)
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
