// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>Native source commitments for an intended live L1 child and original semantic positions.</summary>
/// <remarks>Commitments identify inputs. They do not authenticate the L1 context, signed origin or execution tables.</remarks>
internal sealed class NativeLiveSource
{
    private static readonly ValueHash256 InboundTag = ValueKeccak.Compute("EEZ_ROLLUP0_INBOUND_SOURCE"u8);
    private static readonly ValueHash256 OutboundTag = ValueKeccak.Compute("EEZ_ROLLUPX_OUTBOUND_SOURCE"u8);
    private readonly ulong _targetTimestamp;
    private readonly ValueHash256 _parentHash;

    internal ValueHash256 InboundDomain { get; }
    internal ValueHash256 OutboundDomain { get; }

    internal NativeLiveSource(ulong l1ChainId, Address registry, ulong rollupId, ulong l2ChainId,
        ulong targetTimestamp, in ValueHash256 parentHash)
    {
        if (l1ChainId == 0 || l2ChainId == 0 || rollupId == 0 || registry == Address.Zero || targetTimestamp == 0 || parentHash == default)
            throw new ArgumentException("Invalid native live source context.");
        InboundDomain = Domain(InboundTag, l1ChainId, registry, rollupId, l2ChainId);
        OutboundDomain = Domain(OutboundTag, l1ChainId, registry, rollupId, l2ChainId);
        _targetTimestamp = targetTimestamp;
        _parentHash = parentHash;
    }

    /// <remarks>Indices belong to the complete original stream, before filtering or partial consumption.</remarks>
    internal ValueHash256 Inbound(ulong manifestIndex, ulong topLevelCallIndex, in ValueHash256 callHash)
    {
        Span<byte> bytes = stackalloc byte[6 * AbiWord.Size];
        AbiWriter writer = new(bytes);
        writer.Write(InboundDomain);
        writer.Write(_targetTimestamp);
        writer.Write(_parentHash);
        writer.Write(manifestIndex);
        writer.Write(topLevelCallIndex);
        writer.Write(callHash);
        return ValueKeccak.Compute(bytes);
    }

    /// <remarks>The origin hash covers the exact admitted signed transaction immediately after this load.</remarks>
    internal ValueHash256 Outbound(ulong manifestIndex, in ValueHash256 originTransactionHash)
    {
        Span<byte> bytes = stackalloc byte[5 * AbiWord.Size];
        AbiWriter writer = new(bytes);
        writer.Write(OutboundDomain);
        writer.Write(_targetTimestamp);
        writer.Write(_parentHash);
        writer.Write(manifestIndex);
        writer.Write(originTransactionHash);
        return ValueKeccak.Compute(bytes);
    }

    private static ValueHash256 Domain(in ValueHash256 tag, ulong l1ChainId, Address registry, ulong rollupId, ulong l2ChainId)
    {
        Span<byte> bytes = stackalloc byte[6 * AbiWord.Size];
        AbiWriter writer = new(bytes);
        writer.Write(tag);
        writer.Write(EezConstants.NativeSystemTxVersion);
        writer.Write(l1ChainId);
        writer.Write(registry);
        writer.Write(rollupId);
        writer.Write(l2ChainId);
        return ValueKeccak.Compute(bytes);
    }
}
