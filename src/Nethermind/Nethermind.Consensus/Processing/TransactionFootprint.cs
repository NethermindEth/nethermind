// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>A warm run of one transaction: the state it depended on, the state calls it made and its receipt.</summary>
/// <remarks>
/// Execution decides only on the values it reads, so while every precondition is met, replaying the calls on the
/// block's state is that execution. Balance changes replay as additions and subtractions, so they need no precondition.
/// </remarks>
internal sealed class TransactionFootprint(
    Transaction transaction,
    AccountPrecondition[] accounts,
    SlotPrecondition[] slots,
    StateEffect[] effects,
    in FootprintReceipt receipt,
    in TransactionResult result,
    in ExecutionCounts counts,
    bool recordedOnParentState)
{
    private readonly FootprintReceipt _receipt = receipt;
    private readonly TransactionResult _result = result;
    private readonly ExecutionCounts _counts = counts;

    public Transaction Transaction { get; } = transaction;

    public ref readonly FootprintReceipt Receipt => ref _receipt;

    public ref readonly TransactionResult Result => ref _result;

    /// <summary>What the run added to the execution counters.</summary>
    public ref readonly ExecutionCounts Counts => ref _counts;

    // BENCH (bench/handoff-matches-skip): BENCH_MATCHES_SKIP=0 restores the full comparison for A/B on one image.
    private static readonly bool SkipUnchanged = Environment.GetEnvironmentVariable("BENCH_MATCHES_SKIP") != "0";

    public bool Matches(IWorldState state)
    {
        // A footprint recorded on the block's parent state, which the main thread reads through the same pre-block cache,
        // still holds on an account or a slot the block has not changed so far; only the changed keys need reading. One
        // recorded on the writes of runs warmed before it in the same scope (a sender's later transactions) or on an
        // adjusted sender holds values the parent never had, and is compared in full.
        bool skip = SkipUnchanged && recordedOnParentState;
        long skipped = 0, compared = 0;
        foreach (ref readonly AccountPrecondition account in accounts.AsSpan())
        {
            if (skip && !state.MayHaveChangedInBlock(account.Address)) { skipped++; continue; }
            compared++;
            if (!account.IsMet(state)) { Count(skipped, compared); return false; }
        }

        foreach (ref readonly SlotPrecondition slot in slots.AsSpan())
        {
            if (skip && !state.MayHaveStorageChangedInBlock(in slot.Cell)) { skipped++; continue; }
            compared++;
            state.Get(in slot.Cell, out UInt256 value);
            if (value != slot.Value) { Count(skipped, compared); return false; }
        }

        Count(skipped, compared);
        return true;
    }

    private static void Count(long skipped, long compared)
    {
        Blockchain.Metrics.PrewarmHandoffPreconditionsSkipped += skipped;
        Blockchain.Metrics.PrewarmHandoffPreconditionsChecked += compared;
    }

    public void Replay(IWorldState state, IReleaseSpec spec)
    {
        foreach (ref readonly StateEffect effect in effects.AsSpan())
        {
            effect.Replay(state, spec);
        }
    }
}

internal readonly struct FootprintReceipt(bool success, Address recipient, in GasConsumed gas, LogEntry[] logs, string? error)
{
    public readonly bool Success = success;
    public readonly Address Recipient = recipient;
    public readonly GasConsumed Gas = gas;
    public readonly LogEntry[] Logs = logs;
    public readonly string? Error = error;
}

[Flags]
internal enum AccountFields : byte
{
    None = 0,
    Existence = 1,
    Liveness = 2,
    Balance = 4,
    Nonce = 8,
    Code = 16,
    MinimumBalance = 32
}

/// <summary>An account a run touched, with its values when the transaction started.</summary>
internal struct AccountPrecondition
{
    public Address Address;
    public AccountFields Fields;
    public bool Modified;
    public bool Exists;
    public bool IsDead;
    public ulong Nonce;
    public UInt256 Balance;
    public UInt256 MinimumBalance;
    public ValueHash256 CodeHash;
    public int BalanceValueReads;

    public readonly bool IsMet(IWorldState state)
    {
        AccountFields fields = Fields;
        if (fields == AccountFields.None) return true;
        Address address = Address;
        if ((fields & AccountFields.Existence) != 0 && state.AccountExists(address) != Exists) return false;
        if ((fields & AccountFields.Liveness) != 0 && state.IsDeadAccount(address) != IsDead) return false;
        if ((fields & AccountFields.Nonce) != 0 && state.GetNonce(address) != Nonce) return false;
        if ((fields & (AccountFields.Balance | AccountFields.MinimumBalance)) != 0)
        {
            ref readonly UInt256 balance = ref state.GetBalance(address);
            if (((fields & AccountFields.Balance) != 0 && balance != Balance) || balance < MinimumBalance) return false;
        }

        if ((fields & AccountFields.Code) != 0 && state.GetCodeHash(address) != CodeHash) return false;
        return true;
    }
}

internal struct SlotPrecondition
{
    public StorageCell Cell;
    public UInt256 Value;
    public bool Read;
    public bool Written;
}

internal enum EffectKind : byte
{
    SetStorage,
    ClearStorage,
    MarkStorageDestroyed,
    AddToBalance,
    AddToBalanceAndCreateIfNotExists,
    SubtractFromBalance,
    IncrementNonce,
    DecrementNonce,
    SetNonce,
    CreateAccount,
    CreateAccountIfNotExists,
    DeleteAccount,
    InsertCode
}

internal struct StateEffect
{
    public EffectKind Kind;
    public Address Address;
    public UInt256 Index;
    public UInt256 Value;
    public ulong Nonce;
    public ValueHash256 CodeHash;
    public byte[]? Code;

    public readonly void Replay(IWorldState state, IReleaseSpec spec)
    {
        switch (Kind)
        {
            case EffectKind.SetStorage:
                state.Set(new StorageCell(Address, in Index), in Value);
                break;
            case EffectKind.ClearStorage:
                state.ClearStorage(Address);
                break;
            case EffectKind.MarkStorageDestroyed:
                state.MarkStorageDestroyed(Address);
                break;
            case EffectKind.AddToBalance:
                state.AddToBalance(Address, in Value, spec, out _);
                break;
            case EffectKind.AddToBalanceAndCreateIfNotExists:
                state.AddToBalanceAndCreateIfNotExists(Address, in Value, spec, out _);
                break;
            case EffectKind.SubtractFromBalance:
                state.SubtractFromBalance(Address, in Value, spec, out _);
                break;
            case EffectKind.IncrementNonce:
                state.IncrementNonce(Address, Nonce, out _);
                break;
            case EffectKind.DecrementNonce:
                state.DecrementNonce(Address, Nonce);
                break;
            case EffectKind.SetNonce:
                state.SetNonce(Address, Nonce);
                break;
            case EffectKind.CreateAccount:
                state.CreateAccount(Address, in Value, Nonce);
                break;
            case EffectKind.CreateAccountIfNotExists:
                state.CreateAccountIfNotExists(Address, in Value, Nonce);
                break;
            case EffectKind.DeleteAccount:
                state.DeleteAccount(Address);
                break;
            case EffectKind.InsertCode:
                state.InsertCode(Address, in CodeHash, Code, spec);
                break;
        }
    }
}
