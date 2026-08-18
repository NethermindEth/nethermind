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
/// Each predeploy is described as data — address, code, nonce, and fork gate — so a new one is a list
/// entry rather than another installer. The install is idempotent: a non-empty predeploy re-installs only
/// when the account's code differs from the canonical bytecode or its nonce is below the predeploy nonce;
/// an empty-canonical predeploy (a protocol-managed storage namespace such as EIP-8272's) is gated on the
/// nonce alone, since it writes no code to compare against. The predeploy nonce is 1, matching the
/// EIP-2935/4788/7002/7251 convention. <c>max(existing_nonce, 1)</c> is applied only where a predeploy
/// sets <see cref="Predeploy.PreservesHigherNonce"/> (EIP-8272).
/// </remarks>
public static class PredeployInstaller
{
    private readonly record struct Predeploy(Address Address, ReadOnlyMemory<byte> Code, ulong Nonce, Func<IReleaseSpec, bool> IsActive, bool PreservesHigherNonce = false);

    private static readonly Predeploy[] Predeploys =
    [
        new(Eip8141Constants.ExpiryVerifierAddress, Eip8141Constants.ExpiryVerifierCode, 1, static spec => spec.IsEip8141Enabled),
        new(Eip8250Constants.NonceManagerAddress, Eip8250Constants.NonceManagerCode, 1, static spec => spec.IsEip8250Enabled),
        new(Eip8272Constants.RecentRootAddress, Eip8272Constants.RecentRootCode, 1, static spec => spec.IsEip8272Enabled, PreservesHigherNonce: true),
    ];

    /// <summary>
    /// Returns whether <paramref name="spec"/> activates any declared predeploy, i.e. whether
    /// <see cref="Install"/> can write anything for a block using this spec.
    /// </summary>
    internal static bool HasActivePredeploys(IReleaseSpec spec)
    {
        foreach (Predeploy predeploy in Predeploys)
        {
            if (predeploy.IsActive(spec))
            {
                return true;
            }
        }

        return false;
    }

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
            ulong nonce = readState.GetNonce(predeploy.Address);
            bool codeSatisfied = code.IsEmpty || readState.GetCode(predeploy.Address).AsSpan().SequenceEqual(code.Span);
            if (codeSatisfied && nonce >= predeploy.Nonce)
            {
                continue;
            }

            writeState.CreateAccountIfNotExists(predeploy.Address, UInt256.Zero);
            if (!code.IsEmpty)
            {
                writeState.InsertCode(predeploy.Address, code, spec);
            }

            writeState.SetNonce(predeploy.Address, predeploy.PreservesHigherNonce ? Math.Max(nonce, predeploy.Nonce) : predeploy.Nonce);
        }
    }
}
