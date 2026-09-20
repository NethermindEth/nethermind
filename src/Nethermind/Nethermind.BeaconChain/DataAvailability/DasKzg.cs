// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// This plugin's only point of contact with the KZG trusted setup: the execution layer's own handle,
/// <see cref="KzgPolynomialCommitments.CkzgSetup"/>, made reachable via
/// <c>InternalsVisibleTo</c> rather than loaded again. <see cref="KzgPolynomialCommitments.InitializeAsync"/>
/// is idempotent, so calling it here - needed because a DAS-only code path (e.g. a unit test) may run
/// before the execution layer's own EIP-4844 init step does - never loads a second copy of the file.
/// </summary>
internal static class DasKzg
{
    public static nint Handle
    {
        get
        {
            if (!KzgPolynomialCommitments.IsInitialized)
            {
                KzgPolynomialCommitments.InitializeAsync().GetAwaiter().GetResult();
            }

            return KzgPolynomialCommitments.CkzgSetup;
        }
    }

    /// <summary>Test-only proof hook: true only if <see cref="Handle"/> is the execution layer's exact native pointer, not a copy.</summary>
    internal static bool IsSharedWithExecutionLayerHandle() => Handle == KzgPolynomialCommitments.CkzgSetup;
}
