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
    public const int RecentRootTupleLength = 72;

    public static readonly ValueHash256 RecentRootEntryDomain = ValueKeccak.Compute("RECENT_ROOT_ENTRY");
    public static readonly ValueHash256 RecentRootStorageDomain = ValueKeccak.Compute("RECENT_ROOT_STORAGE");

    public static readonly Address RecentRootAddress = new("0x8272D9679689Ea2f307140CdF9002D27dC00Ffff");

    /// <summary>The runtime code that the EIP-8272 deployment transaction creates at <see cref="RecentRootAddress"/>.</summary>
    public static ReadOnlyMemory<byte> RecentRootCode { get; } = Bytes.FromHexString("0x346100ba57366040146100c05736604836066100ba5780156100ba5761048081116100ba574b60005b602081013560c01c828110156100ba5780830361200011156100ba577f8f42481679c8e6fefa040974b3c905e0ce3f2e464ba93acdb074a41181617efc60005260488260203760686000207fbdc897da2177d260ff5f4be5d4b2aad43f89c3347a305b584fa5a2546d053daa60005290611fff1660c01b60405260486000205414156100ba5760480182811061002857005b60006000fd5b33600052602060006020376034600c20807f8f42481679c8e6fefa040974b3c905e0ce3f2e464ba93acdb074a41181617efc6040524b606852606052602060206088376068604020817fbdc897da2177d260ff5f4be5d4b2aad43f89c3347a305b584fa5a2546d053daa60a852611fff4b1660d05260c852604860a8205500");

    public static ValueHash256 RecentRootCodeHash { get; } = ValueKeccak.Compute(RecentRootCode.Span);
}
