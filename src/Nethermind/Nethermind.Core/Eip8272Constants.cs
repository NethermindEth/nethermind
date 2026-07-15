// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>
/// Parameters for <see href="https://eips.ethereum.org/EIPS/eip-8272">EIP-8272</see> (Recent Roots for Frame Transactions).
/// </summary>
/// <remarks>
/// The EIP is Draft: <c>RECENT_ROOT_ADDRESS</c> and <c>RECENT_ROOT_CODE</c> are still <c>TBD</c> in the
/// specification. The address below mirrors the only existing implementation for cross-client interop and is
/// provisional pending upstream ratification. The <c>RECENTROOTREFLOAD</c> opcode number and the recent-root
/// <c>TXPARAM</c> index are deliberately omitted here: both are contested across the 8141/8250/8272 family and
/// belong to the integration change, not this primitive.
/// </remarks>
public static class Eip8272Constants
{
    /// <summary>
    /// <c>RECENT_ROOT_LENGTH</c>: number of ring-buffer entries retained per source. A slot maps to ring index
    /// <c>slot mod RECENT_ROOT_LENGTH</c>.
    /// </summary>
    public const ulong RecentRootLength = 8192;

    /// <summary>
    /// <c>RECENT_ROOT_USABLE_WINDOW</c>: the maximum age (in slots, inclusive) at which a reference remains valid.
    /// A reference to <c>slot</c> is usable while <c>1 &lt;= current_slot - slot &lt;= RECENT_ROOT_USABLE_WINDOW</c>.
    /// </summary>
    public const ulong RecentRootUsableWindow = 8191;

    /// <summary>
    /// Maximum number of <c>recent_root_references</c> a single frame transaction may carry.
    /// </summary>
    public const int MaxRecentRootReferences = 16;

    /// <summary>
    /// Domain separator for a ring-buffer entry commitment: <c>keccak256("RECENT_ROOT_ENTRY")</c>.
    /// </summary>
    public static readonly ValueHash256 RecentRootEntryDomain = ValueKeccak.Compute("RECENT_ROOT_ENTRY");

    /// <summary>
    /// Domain separator for a ring-buffer storage key: <c>keccak256("RECENT_ROOT_STORAGE")</c>.
    /// </summary>
    public static readonly ValueHash256 RecentRootStorageDomain = ValueKeccak.Compute("RECENT_ROOT_STORAGE");

    /// <summary>
    /// <c>RECENT_ROOT_ADDRESS</c>: the recent-root system contract that holds the per-source ring buffers.
    /// </summary>
    /// <remarks>
    /// Provisional: the specification leaves the address (and <c>RECENT_ROOT_CODE</c>) as <c>TBD</c>. This value
    /// mirrors the sole existing implementation and is subject to change once the EIP is ratified.
    /// </remarks>
    public static readonly Address RecentRootAddress = new("0x0000000000000000000000000000000000008272");
}
