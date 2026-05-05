// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst;

internal static class HsstFlatLayout
{
    /// <summary>
    /// Hard ceiling on the number of summary levels in a FlatEntries HSST. Each level
    /// shrinks by roughly stride/(KeySize+4); 8 levels covers astronomical inputs.
    /// </summary>
    internal const int MaxSummaryDepth = 8;
}
