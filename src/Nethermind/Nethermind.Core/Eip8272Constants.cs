// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core;

/// <summary><see href="https://eips.ethereum.org/EIPS/eip-8272">EIP-8272</see> (Recent Roots for Frame Transactions) parameters.</summary>
public static class Eip8272Constants
{
    public const ulong RecentRootLength = 8192;

    /// <remarks>One less than <see cref="RecentRootLength"/>: a reference of age <see cref="RecentRootLength"/> aliases the current slot's ring-buffer index.</remarks>
    public const ulong RecentRootUsableWindow = RecentRootLength - 1;
    public const int MaxRecentRootReferences = 16;

    public static readonly ValueHash256 RecentRootEntryDomain = ValueKeccak.Compute("RECENT_ROOT_ENTRY");
    public static readonly ValueHash256 RecentRootStorageDomain = ValueKeccak.Compute("RECENT_ROOT_STORAGE");

    // Provisional: spec address is TBD; mirrors the only existing implementation.
    public static readonly Address RecentRootAddress = new("0x0000000000000000000000000000000000008272");

    /// <summary>The runtime code installed at <see cref="RecentRootAddress"/> when EIP-8272 activates.</summary>
    /// <remarks>
    /// Provisional: <c>RECENT_ROOT_CODE</c> is TBD in the spec's constants table, so this is the candidate
    /// proposed in ethereum/EIPs#12131 — the specified write operation assembled verbatim, reading the
    /// current slot through the <see href="https://eips.ethereum.org/EIPS/eip-7843">EIP-7843</see>
    /// <c>SLOTNUM</c> opcode.
    /// </remarks>
    public static ReadOnlyMemory<byte> RecentRootCode { get; } = Bytes.FromHexString(
        "0x341536604014166100105760006000fd5b33600052602060006020376034600c20807f8f42481679c8e6fefa040974b3c905e0ce3f2e464ba93acdb074a41181617efc6040524b606852606052602060206088376068604020817fbdc897da2177d260ff5f4be5d4b2aad43f89c3347a305b584fa5a2546d053daa60a852611fff4b1660d05260c852604860a8205500");
}
