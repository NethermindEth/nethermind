// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.ByPath;
public interface IPathDataCache
{
    void OpenContext(long blockNumber, Keccak parentStateRoot);
    void CloseContext(long blockNumber, Keccak newStateRoot);
    void AddNodeData(long blockNuber, TrieNode node);
    NodeData? GetNodeDataAtRoot(Keccak? rootHash, Span<byte> path);
    NodeData? GetNodeData(Span<byte> path, Keccak? hash);
    bool PersistUntilBlock(long blockNumber, Keccak rootHash, IBatch? batch = null);
    void AddRemovedPrefix(long blockNumber, ReadOnlySpan<byte> keyPrefix);
}
