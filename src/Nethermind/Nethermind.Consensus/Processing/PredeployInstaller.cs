// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Installs the frame-transaction cluster's predeploy runtime code at activation during block
/// processing.
/// </summary>
/// <remarks>
/// Each predeploy is described as data — address, runtime code, nonce, and the fork gate that
/// activates it — so a new predeploy (e.g. a keyed-nonce or recent-roots manager) is added as a list
/// entry rather than another installer and interface method. The install is idempotent: code + nonce
/// are written only when the account's current code differs from the canonical bytecode, so exactly
/// one account update is produced at the first block after each predeploy's activation and none
/// afterwards. The predeploy nonce is 1, matching the EIP-2935/4788/7002/7251 predeploy convention,
/// so the resulting state root and the EIP-7928 block-level access list agree across clients.
/// </remarks>
public static class PredeployInstaller
{
    private readonly record struct Predeploy(Address Address, ReadOnlyMemory<byte> Code, ulong Nonce, Func<IReleaseSpec, bool> IsActive);

    private static readonly Predeploy[] Predeploys =
    [
        new(Eip8141Constants.ExpiryVerifierAddress, Eip8141Constants.ExpiryVerifierCode, 1, static spec => spec.IsEip8141Enabled),
        new(Eip8250Constants.NonceManagerAddress, Eip8250Constants.NonceManagerCode, 1, static spec => spec.IsEip8250Enabled),
    ];

    /// <summary>
    /// Ensures every predeploy activated by <paramref name="spec"/> has its canonical code + nonce present.
    /// </summary>
    /// <remarks>
    /// The idempotency probe reads the current code from <paramref name="readState"/> (which must not
    /// record into the block-level access list) so that on a no-op block nothing is written to
    /// <paramref name="writeState"/> and no BAL entry is produced. On the standard, non-BAL path the
    /// same world state is passed for both.
    /// </remarks>
    /// <param name="readState">Untraced state used only to decide whether an install is required.</param>
    /// <param name="writeState">State the code + nonce change is applied to (BAL-traced on the BAL path).</param>
    /// <param name="spec">The release spec in effect for the block being processed.</param>
    public static void Install(IReadOnlyStateProvider readState, IWorldState writeState, IReleaseSpec spec)
    {
        foreach (Predeploy predeploy in Predeploys)
        {
            if (!predeploy.IsActive(spec))
            {
                continue;
            }

            ReadOnlyMemory<byte> code = predeploy.Code;
            if (readState.GetCode(predeploy.Address).AsSpan().SequenceEqual(code.Span))
            {
                continue;
            }

            writeState.CreateAccountIfNotExists(predeploy.Address, UInt256.Zero);
            writeState.InsertCode(predeploy.Address, code, spec);
            writeState.SetNonce(predeploy.Address, predeploy.Nonce);
        }
    }
}
