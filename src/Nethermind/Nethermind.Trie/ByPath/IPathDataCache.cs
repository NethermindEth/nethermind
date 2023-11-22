// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Trie.ByPath;
public interface IPathDataCache
{
    void OpenContext(long blockNumber, Hash256 parentStateRoot);
    void CloseContext(long blockNumber, Hash256 newStateRoot);
    void AddNodeData(long blockNuber, TrieNode node);
    NodeData? GetNodeDataAtRoot(Hash256? rootHash, Span<byte> path);
    NodeData? GetNodeData(Span<byte> path, Hash256? hash);
    bool PersistUntilBlock(long blockNumber, Hash256 rootHash, IWriteBatch? batch = null);
    void AddRemovedPrefix(long blockNumber, ReadOnlySpan<byte> keyPrefix);
}
