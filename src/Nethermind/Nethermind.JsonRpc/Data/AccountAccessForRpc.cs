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

    private static AccountAccessForRpc FromAccountChanges(ReadOnlyAccountChanges account) =>
        new()
        {
            Address = account.Address,
            StorageChanges = Map(account.StorageChanges, static sc => new SlotChangesForRpc
            {
                Key = sc.Key.ToValueHash(),
                Changes = Map(sc.Changes, static c => new StorageChangeForRpc { Index = c.Index, Value = ToValueHash(c.Value) }),
            }),
            StorageReads = Map(account.StorageReads, static r => r.ToValueHash()),
            BalanceChanges = Map(account.BalanceChanges, static c => new BalanceChangeForRpc { Index = c.Index, Value = c.Value }),
            NonceChanges = Map(account.NonceChanges, static c => new NonceChangeForRpc { Index = c.Index, Value = c.Value }),
            CodeChanges = Map(account.CodeChanges, static c => new CodeChangeForRpc { Index = c.Index, Code = c.Code }),
        };

    /// <summary>Projects each element of <paramref name="source"/>, reusing a shared empty array when empty.</summary>
    private static TResult[] Map<TSource, TResult>(TSource[] source, Func<TSource, TResult> selector)
    {
        if (source.Length == 0) return [];

        TResult[] result = new TResult[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = selector(source[i]);
        }

        return result;
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
