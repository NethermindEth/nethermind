// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Which prefixless, leaf-free interior branches a rewritten node group leaves implicit.</summary>
/// <remarks>Every value produces the same hashes and is readable by the same reader, which rebuilds an absent branch from its children.</remarks>
public enum PbtPrefixlessBranchOmission
{
    /// <summary>Store every node.</summary>
    None,

    /// <summary>Omit relative depths 1–3, so a dense group holds only its 16 boundary nodes.</summary>
    Interior,

    /// <summary>Omit relative depths 1 and 3, keeping depth 2 so an omitted branch is rebuilt from stored children.</summary>
    OddLevels,
}
