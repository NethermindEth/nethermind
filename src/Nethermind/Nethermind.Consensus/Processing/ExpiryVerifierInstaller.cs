// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Installs the EIP-8141 expiry-verifier predeploy runtime code at
/// <see cref="Eip8141Constants.ExpiryVerifierAddress"/> during block processing.
/// </summary>
/// <remarks>
/// EIP-8141 mandates that the expiry-verifier contract runtime code is present at activation. The
/// install is idempotent: code + nonce are written only when the account's current code differs
/// from the canonical bytecode, so exactly one account update is produced at the first
/// post-activation block and none afterwards. The predeploy nonce is set to 1, matching the
/// EIP-2935/4788/7002/7251 predeploy convention. This reproduces ethrex's
/// <c>install_expiry_verifier_code</c> so the resulting state root and the EIP-7928 block-level
/// access list agree cross-client.
/// </remarks>
public static class ExpiryVerifierInstaller
{
    private const ulong PredeployNonce = 1;

    /// <summary>
    /// Ensures the expiry-verifier code + nonce are present at
    /// <see cref="Eip8141Constants.ExpiryVerifierAddress"/>.
    /// </summary>
    /// <remarks>
    /// The idempotency probe reads the current code from <paramref name="readState"/> (which must
    /// not record into the block-level access list) so that on a no-op block nothing is written to
    /// <paramref name="writeState"/> and no BAL entry is produced. On the standard, non-BAL path
    /// the same world state is passed for both.
    /// </remarks>
    /// <param name="readState">Untraced state used only to decide whether an install is required.</param>
    /// <param name="writeState">State the code + nonce change is applied to (BAL-traced on the BAL path).</param>
    /// <param name="spec">The release spec in effect for the block being processed.</param>
    public static void Install(IReadOnlyStateProvider readState, IWorldState writeState, IReleaseSpec spec)
    {
        Address address = Eip8141Constants.ExpiryVerifierAddress;
        ReadOnlyMemory<byte> code = Eip8141Constants.ExpiryVerifierCode;

        if (readState.GetCode(address).AsSpan().SequenceEqual(code.Span))
        {
            return;
        }

        writeState.CreateAccountIfNotExists(address, UInt256.Zero);
        writeState.InsertCode(address, code, spec);
        writeState.SetNonce(address, PredeployNonce);
    }
}
