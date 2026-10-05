// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>Installs the frame-transaction cluster's predeploy runtime code at activation.</summary>
/// <remarks>A predeploy with empty canonical code has nothing to compare against, so it must declare a nonce
/// or it is never installed. The nonce of 1 follows the EIP-2935/4788/7002/7251 convention.</remarks>
public static class PredeployInstaller
{
    /// <param name="InitializesOnce">EIP-8272: the predeploy is initialised only over an account with empty code and
    /// storage, and never again. An account already holding code is treated as initialised, as the parent spec is not
    /// available here to tell the first active block apart.</param>
    private readonly record struct Predeploy(Address Address, ReadOnlyMemory<byte> Code, ulong? Nonce, Func<IReleaseSpec, bool> IsActive, bool PreservesHigherNonce = false, bool InitializesOnce = false);

    private static readonly Predeploy[] Predeploys =
    [
        // EIP-8141 mandates the runtime code only, leaving the account's other fields; installing at the fork
        // is a stop-gap, and deploying it like the other system contracts later would give it a nonce.
        new(Eip8141Constants.ExpiryVerifierAddress, Eip8141Constants.ExpiryVerifierCode, null, static spec => spec.IsEip8141Enabled),
        new(Eip8250Constants.NonceManagerAddress, Eip8250Constants.NonceManagerCode, 1, static spec => spec.IsEip8250Enabled, PreservesHigherNonce: true),
        new(Eip8272Constants.RecentRootAddress, Eip8272Constants.RecentRootCode, 1, static spec => spec.IsEip8272Enabled, PreservesHigherNonce: true, InitializesOnce: true),
    ];

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

    /// <summary>Ensures every predeploy activated by <paramref name="spec"/> has its canonical code and nonce.</summary>
    /// <param name="readState">Untraced state probed to decide whether an install is needed, so that a no-op
    /// block produces no BAL entry; on the non-BAL path the same world state is passed for both.</param>
    /// <param name="writeState">State the code and nonce change is applied to (BAL-traced on the BAL path).</param>
    /// <param name="spec">The release spec in effect for the block being processed.</param>
    /// <returns><see langword="false"/> when a predeploy that initialises once finds storage without code, which makes
    /// the block invalid.</returns>
    public static bool Install(IReadOnlyStateProvider readState, IWorldState writeState, IReleaseSpec spec)
    {
        foreach (Predeploy predeploy in Predeploys)
        {
            if (!predeploy.IsActive(spec))
            {
                continue;
            }

            ReadOnlyMemory<byte> code = predeploy.Code;
            ulong nonce = readState.GetNonce(predeploy.Address);
            ReadOnlySpan<byte> existingCode = readState.GetCodeSpan(predeploy.Address);
            if (predeploy.InitializesOnce)
            {
                if (!existingCode.IsEmpty)
                {
                    continue;
                }

                if (readState.TryGetAccount(predeploy.Address, out AccountStruct account) && account.HasStorage)
                {
                    return false;
                }
            }

            bool codeSatisfied = code.IsEmpty || existingCode.SequenceEqual(code.Span);
            if (codeSatisfied && (predeploy.Nonce is not ulong required || nonce >= required))
            {
                continue;
            }

            writeState.CreateAccountIfNotExists(predeploy.Address, UInt256.Zero);
            if (!code.IsEmpty)
            {
                writeState.InsertCode(predeploy.Address, code, spec);
            }

            if (predeploy.Nonce is ulong predeployNonce)
            {
                writeState.SetNonce(predeploy.Address, predeploy.PreservesHigherNonce ? Math.Max(nonce, predeployNonce) : predeployNonce);
            }
        }

        return true;
    }
}
