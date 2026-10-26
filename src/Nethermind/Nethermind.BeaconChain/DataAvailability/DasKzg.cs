// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>Loads the execution layer's KZG trusted setup on first use by this plugin's DAS code paths.</summary>
internal static class DasKzg
{
    /// <summary>Loads the execution layer's KZG trusted setup if nothing has loaded it yet.</summary>
    /// <remarks>
    /// A DAS-only code path (e.g. a unit test) may run before the execution layer's own EIP-4844 init step does.
    /// <see cref="KzgPolynomialCommitments.InitializeAsync"/> is idempotent, so this never loads a second copy of the setup.
    /// </remarks>
    public static void EnsureInitialized()
    {
        if (!KzgPolynomialCommitments.IsInitialized)
        {
            KzgPolynomialCommitments.InitializeAsync().GetAwaiter().GetResult();
        }
    }
}
