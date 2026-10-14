// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.Precompiles;

/// <summary>Whether the guest's zkVM takes BLS12-381 points in SP1's byte layout.</summary>
/// <remarks>
/// The accelerator standard leaves the point layout open. ZisK and OpenVM take EIP-2537's order (each Fp2 as
/// <c>c0 || c1</c>, the point at infinity as zeros); SP1's libzkevm takes zkcrypto's uncompressed encoding
/// (<c>c1 || c0</c>, infinity as a flag bit in the first byte) and rejects any other G2 point as off the curve.
/// <see cref="IsActive"/> is <see langword="false"/> as written; the SP1 guest's <c>substitutions.xml</c>
/// stubs it to <see langword="true"/> at link time, and ILC folds every check.
/// </remarks>
internal readonly struct Sp1Bls12381LayoutFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
