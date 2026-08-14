// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    public static readonly Address RecentRootAddress = new("0x0000000000000000000000000000000000008272");

    /// <summary>The runtime code installed at <see cref="RecentRootAddress"/> when EIP-8272 activates.</summary>
    /// <remarks>
    /// Provisional: <c>RECENT_ROOT_CODE</c> is TBD in the spec's constants table, and unlike the address
    /// there is no reference implementation value to mirror.
    /// The predeploy is only ever a protocol-managed
    /// storage namespace: recent-root entries are written natively and no call ever enters the account.
    /// Empty code keeps the activation account state minimal and matches the only existing implementation.
    /// </remarks>
    public static ReadOnlyMemory<byte> RecentRootCode { get; } = ReadOnlyMemory<byte>.Empty;
}
