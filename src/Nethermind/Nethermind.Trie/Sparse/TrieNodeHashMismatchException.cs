// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// Thrown when a trie node loaded from storage has RLP whose keccak hash does not match the expected hash.
/// Indicates data corruption or stale path-based storage.
/// </summary>
public class TrieNodeHashMismatchException(
    TreePath path,
    Hash256 expectedHash,
    Hash256 actualHash,
    Hash256? address = null)
    : TrieException($"Trie node hash mismatch at path {path}: expected {expectedHash}, got {actualHash}")
{
    public TreePath Path { get; } = path;
    public Hash256 ExpectedHash { get; } = expectedHash;
    public Hash256 ActualHash { get; } = actualHash;
    public Hash256? Address { get; } = address;
}
