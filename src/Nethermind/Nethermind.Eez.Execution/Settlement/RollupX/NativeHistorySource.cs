// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>History occurrence identity bound to genesis and exact L2 position, independent of settlement attempts.</summary>
internal sealed class NativeHistorySource
{
    private static readonly ValueHash256 Tag = ValueKeccak.Compute("EEZ_ROLLUPX_HISTORY_SOURCE"u8);
    internal ValueHash256 Domain { get; }

    internal NativeHistorySource(ulong l1ChainId, ulong l2ChainId, in ValueHash256 genesisHash)
    {
        if (l1ChainId == 0 || l2ChainId == 0 || genesisHash == default)
            throw new ArgumentException("Invalid native history source context.");
        Span<byte> bytes = stackalloc byte[6 * AbiWord.Size];
        AbiWriter writer = new(bytes);
        writer.Write(Tag);
        writer.Write(EezConstants.NativeSystemTxVersion);
        writer.Write(l1ChainId);
        writer.Write(l2ChainId);
        writer.Write(genesisHash);
        writer.Write(EezConstants.L1HistoryAddress);
        Domain = ValueKeccak.Compute(bytes);
    }

    /// <remarks>Parent relation, schedule, history state and authenticated L1 ancestry require separate validation.</remarks>
    internal ValueHash256 Compute(in ValueHash256 parentHash, ulong number, ulong timestamp, ReadOnlySpan<byte> importCalldata)
    {
        if (parentHash == default || number == 0 || timestamp == 0)
            throw new ArgumentException("Invalid native history position.");
        NativeHistoryCall.Decode(importCalldata, out _);
        Span<byte> bytes = stackalloc byte[5 * AbiWord.Size];
        AbiWriter writer = new(bytes);
        writer.Write(Domain);
        writer.Write(parentHash);
        writer.Write(number);
        writer.Write(timestamp);
        writer.Write(ValueKeccak.Compute(importCalldata));
        return ValueKeccak.Compute(bytes);
    }
}
