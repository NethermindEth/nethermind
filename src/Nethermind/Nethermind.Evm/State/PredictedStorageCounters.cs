// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>Experiment only: an account change a footprint makes, kept in transaction order.</summary>
public enum PredictedAccountOp : byte
{
    AddBalance,
    AddBalanceCreate,
    SubtractBalance,
    IncrementNonce,
    DecrementNonce,
    SetNonce,
    Create,
    CreateIfNotExists,
    Delete,
    InsertCode,
}

/// <summary>Experiment only: one account change of a footprint.</summary>
public readonly record struct PredictedAccountEffect(PredictedAccountOp Op, UInt256 Value, ulong Nonce, ValueHash256 CodeHash);

/// <summary>Experiment only: what the predicted storage trees came to, summed over the process.</summary>
public static class PredictedStorageCounters
{
    /// <summary>
    /// NETHERMIND_EXP_FOOTPRINT_ROOTS=dry: the predicted writes are kept and compared with each account's write batch,
    /// and the batch's trie work is timed; no tree is built ahead and nothing is adopted.
    /// </summary>
    public static readonly bool DryRun = Environment.GetEnvironmentVariable("NETHERMIND_EXP_FOOTPRINT_ROOTS") == "dry";

    public static long Built, BuiltWrites, BuildTicks, Adopted, Unclaimed, StaleBase, Late;

    // Dry run, by account write batch: predicted and exact (every write as predicted, no predicted write left over at
    // another value), predicted and not, and without a prediction; the writes and trie ticks of each, and per-block maxima.
    public static long DryExactAccounts, DryExactWrites, DryExactTicks, DryExactMaxTicks;
    public static long DryInexactAccounts, DryInexactWrites, DryInexactMatched, DryInexactLeftovers, DryInexactTicks, DryInexactMaxTicks;
    public static long UnpredictedAccounts, UnpredictedWrites, UnpredictedTicks, UnpredictedMaxTicks;

    // Dry run, by account the state write batch sets: its nonce, balance, code and existence as the folded footprints
    // predict them, or not, or with no prediction; predicted changes the block did not make; the account trie update.
    public static long DryAccountsExact, DryAccountsInexact, DryAccountsUnpredicted, DryAccountsLeftover, DryAccountsTotal, DryStateSetTicks;

    // The account trie update built ahead from the footprints: builds, the accounts in them, their ticks; adoptions, the
    // accounts the write batch then still set, the ones it kept from the prediction; builds that arrived too late.
    public static long StatesBuilt, StateAccounts, StateBuildTicks, StatesAdopted, StateAccountsSet, StateAccountsKept, StatesLate, StateSetTicks;

    public static void Max(ref long target, long value)
    {
        long current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
