// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

public partial record struct RlpLimit(int Limit, string TypeName = "", ReadOnlyMemory<char> PropertyName = default)
{
    private const ulong DefaultMaxBlockGas = 1_000_000_000;

    /// <remarks>
    /// Should not be captured in a static readonly field - the value is set from
    /// client configuration during startup, type initializers can run before.
    /// </remarks>
    public static ulong MaxBlockGas { get; private set; } = DefaultMaxBlockGas;

    public static void InitMaxBlockGas(ulong maxBlockGas)
    {
        MaxBlockGas = maxBlockGas;
        ReceiptLogs = ForReceiptLogs(maxBlockGas);
    }

    private static RlpLimit ForReceiptLogs(ulong maxBlockGas) =>
        For<TxReceipt>((int)ulong.Min(maxBlockGas / GasCostOf.Log + 1, int.MaxValue - 1), nameof(TxReceipt.Logs));

    public RlpLimit() : this((int)4.MiB) { }

    public string CollectionExpression
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => field ??= GenerateCollectionExpression();
    }

    private string GenerateCollectionExpression() =>
        PropertyName.IsEmpty
            ? TypeName
            : $"{TypeName}.{PropertyName}";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RlpLimit For<T>(int limit, string propertyName = "") => new(limit, typeof(T).Name, propertyName.AsMemory());

    public override string ToString() => CollectionExpression;
}
