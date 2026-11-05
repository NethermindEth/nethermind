// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary><see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see> (Keyed Nonces) parameters.</summary>
public static class Eip8250Constants
{
    public const int MaxNonceKeys = 16;
    public const ulong MaxNonceSeq = ulong.MaxValue;

    /// <summary><c>KEYED_NONCE_ACCESS_COST</c>: the cold read and the write of one <c>NONCE_MANAGER</c> slot.</summary>
    public const ulong KeyedNonceAccessCost = Eip8038Constants.ColdStorageAccess + Eip8038Constants.StorageWrite;

    public static readonly Address NonceManagerAddress = new("0x8250968C12e01A19d6F667b9B2F3b3A4d0e51cB7");
}
