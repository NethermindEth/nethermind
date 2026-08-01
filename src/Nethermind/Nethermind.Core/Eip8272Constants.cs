// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

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

    /// <remarks>Provisional: the spec leaves RECENT_ROOT_CODE TBD, so the write is native and priced as
    /// the cold account access plus the storage write that real predeploy bytecode would perform.</remarks>
    public const ulong RecentRootWriteGas = 22100;

    public const int RecentRootWriteCalldataLength = 64;
}
