// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary><see href="https://eips.ethereum.org/EIPS/eip-8272">EIP-8272</see> (Recent Roots for Frame Transactions) parameters.</summary>
public static class Eip8272Constants
{
    public const ulong RecentRootLength = 8192;
    public const ulong RecentRootUsableWindow = 8191;
    public const int MaxRecentRootReferences = 16;

    public static readonly ValueHash256 RecentRootEntryDomain = ValueKeccak.Compute("RECENT_ROOT_ENTRY");
    public static readonly ValueHash256 RecentRootStorageDomain = ValueKeccak.Compute("RECENT_ROOT_STORAGE");

    // Provisional: spec address is TBD; mirrors the only existing implementation.
    public static readonly Address RecentRootAddress = new("0x0000000000000000000000000000000000008272");
}
