// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst;

internal static class HsstPackedArrayLayout
{
    /// <summary>
    /// Hard ceiling on the number of summary levels in a PackedArray HSST. With the 1 KiB
    /// default stride, realistic Nethermind inputs (KeySize ≤ 32, EntryCount in the tens
    /// of millions) stay at depth ≤ 4. Inputs that would push past this throw at build.
    /// </summary>
    internal const int MaxSummaryDepth = 4;
}
