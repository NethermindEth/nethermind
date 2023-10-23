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
    void SetContext(Keccak keccak);
    bool EnsureStateHistoryExists(long blockNuber, Keccak stateHash);
    void AddNodeData(long blockNuber, Keccak stateHash, TrieNode node);
    void AddNodeDataTransient(TrieNode node);
    NodeData? GetNodeDataAtRoot(Keccak? rootHash, Span<byte> path);
    NodeData? GetNodeData(Span<byte> path, Keccak? hash);
    bool PersistUntilBlock(long blockNumber, Keccak rootHash, IBatch? batch = null);
    void MoveTransientData(long blockNumber, Keccak stateRoot);
}
