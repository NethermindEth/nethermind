// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-7805">EIP-7805</see>
/// (fork-choice enforced inclusion lists, FOCIL) parameters.
/// </summary>
public static class Eip7805Constants
{
    /// <summary>The <c>MAX_BYTES_PER_INCLUSION_LIST</c> parameter.</summary>
    public const int MaxBytesPerInclusionList = 8192;
}
