// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Account entry of an EIP-7928 block access list as returned by <c>eth_getBlockAccessList</c>,
/// matching the execution-apis <c>AccountAccess</c> schema.
/// </summary>
public class AccountAccessForRpc
{
    public required Address Address { get; init; }
    public required SlotChangesForRpc[] StorageChanges { get; init; }
    public required ValueHash256[] StorageReads { get; init; }
    public required BalanceChangeForRpc[] BalanceChanges { get; init; }
    public required NonceChangeForRpc[] NonceChanges { get; init; }
    public required CodeChangeForRpc[] CodeChanges { get; init; }

    public static AccountAccessForRpc[] FromBlockAccessList(ReadOnlyBlockAccessList blockAccessList)
    {
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = blockAccessList.AccountChanges.AsSpan();
        AccountAccessForRpc[] result = new AccountAccessForRpc[accounts.Length];
        for (int i = 0; i < accounts.Length; i++)
        {
            result[i] = FromAccountChanges(accounts[i]);
        }

        return result;
    }

    private static AccountAccessForRpc FromAccountChanges(ReadOnlyAccountChanges account)
    {
        SlotChangesForRpc[] storageChanges = new SlotChangesForRpc[account.StorageChanges.Length];
        for (int i = 0; i < storageChanges.Length; i++)
        {
            ReadOnlySlotChanges slotChanges = account.StorageChanges[i];
            StorageChangeForRpc[] changes = new StorageChangeForRpc[slotChanges.Changes.Length];
            for (int j = 0; j < changes.Length; j++)
            {
                StorageChange change = slotChanges.Changes[j];
                changes[j] = new StorageChangeForRpc { Index = change.Index, Value = ToValueHash(change.Value) };
            }

            storageChanges[i] = new SlotChangesForRpc { Key = slotChanges.Key.ToValueHash(), Changes = changes };
        }

        ValueHash256[] storageReads = new ValueHash256[account.StorageReads.Length];
        for (int i = 0; i < storageReads.Length; i++)
        {
            storageReads[i] = account.StorageReads[i].ToValueHash();
        }

        BalanceChangeForRpc[] balanceChanges = new BalanceChangeForRpc[account.BalanceChanges.Length];
        for (int i = 0; i < balanceChanges.Length; i++)
        {
            BalanceChange change = account.BalanceChanges[i];
            balanceChanges[i] = new BalanceChangeForRpc { Index = change.Index, Value = change.Value };
        }

        NonceChangeForRpc[] nonceChanges = new NonceChangeForRpc[account.NonceChanges.Length];
        for (int i = 0; i < nonceChanges.Length; i++)
        {
            NonceChange change = account.NonceChanges[i];
            nonceChanges[i] = new NonceChangeForRpc { Index = change.Index, Value = change.Value };
        }

        CodeChangeForRpc[] codeChanges = new CodeChangeForRpc[account.CodeChanges.Length];
        for (int i = 0; i < codeChanges.Length; i++)
        {
            CodeChange change = account.CodeChanges[i];
            codeChanges[i] = new CodeChangeForRpc { Index = change.Index, Code = change.Code };
        }

        return new AccountAccessForRpc
        {
            Address = account.Address,
            StorageChanges = storageChanges,
            StorageReads = storageReads,
            BalanceChanges = balanceChanges,
            NonceChanges = nonceChanges,
            CodeChanges = codeChanges,
        };
    }

    // EvmWord already holds the 32 big-endian bytes.
    private static ValueHash256 ToValueHash(in EvmWord word) => Unsafe.BitCast<EvmWord, ValueHash256>(word);
}

public readonly struct SlotChangesForRpc
{
    public required ValueHash256 Key { get; init; }
    public required StorageChangeForRpc[] Changes { get; init; }
}

public readonly struct StorageChangeForRpc
{
    public required ulong Index { get; init; }
    public required ValueHash256 Value { get; init; }
}

public readonly struct BalanceChangeForRpc
{
    public required ulong Index { get; init; }
    public required UInt256 Value { get; init; }
}

public readonly struct NonceChangeForRpc
{
    public required ulong Index { get; init; }
    public required ulong Value { get; init; }
}

public readonly struct CodeChangeForRpc
{
    public required ulong Index { get; init; }
    public required byte[] Code { get; init; }
}
