// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Specs;

public static partial class ReleaseSpecExtensions
{
    /// <summary>
    /// Returns the spec variant to use when executing a system transaction.
    /// </summary>
    /// <remarks>
    /// <paramref name="isGenesis"/> short-circuits to the unwrapped spec — genesis system
    /// transactions on standard chains run with EIP-158 still on. Callers that need EIP-158
    /// disabled even at genesis (e.g. AuRa system contracts) pass <c>false</c>.
    /// </remarks>
    public static IReleaseSpec ForSystemTransaction(this IReleaseSpec spec, bool isGenesis) =>
        spec switch
        {
            ReleaseSpec releaseSpec => releaseSpec.SystemSpec,
            { IsEip158Enabled: false } => spec,
            _ when isGenesis => spec,
            _ => WithoutEip158ForSystemTransaction(spec)
        };
}
