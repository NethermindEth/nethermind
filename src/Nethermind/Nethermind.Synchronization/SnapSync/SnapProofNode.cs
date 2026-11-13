// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>A node of a range proof that the range's trie is built over, as <see cref="SnapProviderHelper"/> trims it.</summary>
/// <param name="ProofIndex">The index of the node's RLP in the proofs.</param>
/// <param name="Path">The node's path.</param>
/// <param name="ParentIndex">The index, in the same list, of the proof node it is a child of, or -1 for the root.</param>
/// <param name="ChildIndex">The node's index in its parent: the branch child, or 0 below an extension.</param>
/// <param name="ClearedChildren">Bit i set for each branch child dropped, which the range's entries rebuild.</param>
public readonly record struct SnapProofNode(int ProofIndex, TreePath Path, int ParentIndex, int ChildIndex, ushort ClearedChildren);
