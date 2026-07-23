// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Core;

/// <summary>
/// Constants of EIP-8250 keyed nonces.
/// https://eips.ethereum.org/EIPS/eip-8250
/// </summary>
public static class Eip8250Constants
{
    /// <summary>
    /// The <c>NONCE_MANAGER</c> system contract address. The spec value is <c>TBD</c>; this provisional
    /// address MUST be replaced with the finalized one, which has to be selected so that no code or
    /// storage exists at it on every intended activation network at fork-configuration finalization.
    /// </summary>
    public static readonly Address NonceManagerAddress = new("0x0000000000000000000000000000000000008250");

    /// <summary>
    /// The <c>NONCE_MANAGER</c> runtime code, a runtime equivalent to <c>revert(0, 0)</c>: any ordinary
    /// call reverts with empty returndata. Installed at <see cref="NonceManagerAddress"/> when EIP-8250
    /// activates.
    /// </summary>
    public static readonly byte[] NonceManagerCode = Bytes.FromHexString("0x60006000fd");
}
