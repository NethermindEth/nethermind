// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>Identifies complete native groups in the original Live Sync block.</summary>
/// <remarks>Source identity does not authenticate execution, settlement placement or a consumed prefix.</remarks>
internal sealed class NativeLiveSource
{
    private static ReadOnlySpan<byte> Domain => "SOURCE_ENTRY_V1"u8;
    private readonly ulong _chainId;
    private readonly ValueHash256 _parentHash;

    internal NativeLiveSource(ulong chainId, in ValueHash256 parentHash)
    {
        if (chainId == 0 || parentHash == default) throw new ArgumentException("Invalid native live source context.");
        _chainId = chainId;
        _parentHash = parentHash;
    }

    internal ulong ChainId => _chainId;

    /// <remarks>The ordinal counts complete applying groups before any consumed suffix is selected.</remarks>
    internal ValueHash256 Entry(ulong index)
    {
        Span<byte> bytes = stackalloc byte[6 * AbiWord.Size];
        WriteEntry(bytes, index);
        return ValueKeccak.Compute(bytes);
    }

    internal byte[] EncodeEntry(ulong index)
    {
        byte[] bytes = new byte[6 * AbiWord.Size];
        WriteEntry(bytes, index);
        return bytes;
    }

    private void WriteEntry(Span<byte> bytes, ulong index)
    {
        AbiWriter writer = new(bytes);
        writer.WriteOffset(4 * AbiWord.Size);
        writer.Write(_chainId);
        writer.Write(_parentHash);
        writer.Write(index);
        writer.WriteBytes(Domain);
    }
}
