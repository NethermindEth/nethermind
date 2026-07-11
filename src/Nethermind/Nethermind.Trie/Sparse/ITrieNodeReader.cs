// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// Reads trie node RLP from persistent storage. Both path AND hash are required because HalfPath
/// keys include the hash. Flat DB backends may ignore the hash but must validate it post-load.
/// <remarks>
/// All methods return non-null. Missing nodes throw <see cref="MissingTrieNodeException"/>.
/// Hash mismatches throw <see cref="TrieNodeHashMismatchException"/>.
/// </remarks>
/// </summary>
public interface ITrieNodeReader
{
    byte[] LoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
    byte[] LoadStorageRlp(Hash256 accountPathHash, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);
}
