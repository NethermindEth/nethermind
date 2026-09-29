// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Eez.Execution.Settlement;

namespace Nethermind.Eez.Posting;

/// <summary>
/// The gas a postBatch needs, with the reference composer's pins measured on the EEZ contract, so a batch too large for
/// its gas limit is cut down before any attester is asked to sign it.
/// </summary>
public static class PostBatchGas
{
    /// <summary>The EIP-7825 cap on one transaction's gas, above which no transaction is valid.</summary>
    public const ulong DefaultLimit = 16_777_216;

    /// <summary>Execution of a batch with its anchor entry alone, calldata aside.</summary>
    public const ulong BasePin = 160_000;

    /// <summary>Each entry: its hashing, a state write and, for a new sender, a proxy deploy.</summary>
    public const ulong EntryPin = 370_000;

    /// <summary>Each proof system past the first: a verification key read and a verify call.</summary>
    public const ulong ProofSystemPin = 14_000;

    /// <summary>What each proof is sized as before the attesters return it; an ECDSA proof is 65 bytes.</summary>
    public const int ProofStandIn = 128;

    private const ulong TransactionBase = 21_000;

    /// <summary>The larger of the standard and the EIP-7623 floor gas of <paramref name="batch"/> carrying one proof per proof system.</summary>
    public static ulong Needed(PostBatch batch, int proofSystems)
    {
        byte[][] standIns = new byte[proofSystems][];
        for (int i = 0; i < standIns.Length; i++)
        {
            standIns[i] = new byte[ProofStandIn];
            Array.Fill(standIns[i], (byte)0xff);
        }

        byte[] calldata = EezCalldata.EncodePostAndVerifyBatch(batch with { Proofs = standIns });
        int nonzero = 0;
        foreach (byte b in calldata)
        {
            nonzero += b == 0 ? 0 : 1;
        }

        ulong zero = (ulong)(calldata.Length - nonzero);
        ulong standard = TransactionBase + BasePin + EntryPin * (ulong)batch.Entries.Length + 4 * zero + 16 * (ulong)nonzero
            + ProofSystemPin * (ulong)Math.Max(proofSystems - 1, 0);
        ulong floor = TransactionBase + 10 * (zero + 4 * (ulong)nonzero);
        return Math.Max(standard, floor);
    }
}
