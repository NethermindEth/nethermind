// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Trie;

/// <summary>Whether the guest encodes a branch it hashes at once into scratch rather than keeping the new RLP.</summary>
/// <remarks>
/// <see langword="false"/> as written; the SP1 guest's <c>substitutions.xml</c> stubs <see cref="IsActive"/> to
/// <see langword="true"/> at link time and ILC folds every check on it. SP1 charges memory by the words a run first
/// touches, which a fresh buffer per encoded branch keeps adding to; the other guests pay more for the scratch
/// bookkeeping than they save. See <c>TrieNode.KeepsRlpOfHashedBranch</c>.
/// </remarks>
internal readonly struct ScratchBranchRlpFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
