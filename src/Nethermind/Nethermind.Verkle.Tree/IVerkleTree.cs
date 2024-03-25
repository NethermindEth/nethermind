// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.Sync;

namespace Nethermind.Verkle.Tree;

public interface IVerkleTree
{
    public Hash256 StateRoot { get; set; }
    public bool MoveToStateRoot(Hash256 stateRoot);
    public byte[]? Get(Hash256 key, Hash256? stateRoot = null);
    public void Insert(Hash256 key, in ReadOnlySpan<byte> value);
    public void InsertStemBatch(in ReadOnlySpan<byte> stem, IEnumerable<(byte, byte[])> leafIndexValueMap);
    public void InsertStemBatch(in ReadOnlySpan<byte> stem, IEnumerable<LeafInSubTree> leafIndexValueMap);
    public void InsertStemBatch(Stem stem, IEnumerable<LeafInSubTree> leafIndexValueMap);
    public void Commit(bool forSync = false);
    public void CommitTree(long blockNumber);
    public void Accept(ITreeVisitor visitor, Hash256 rootHash, VisitingOptions? visitingOptions = null);
    public void Accept(IVerkleTreeVisitor visitor, Hash256 rootHash, VisitingOptions? visitingOptions = null);
}
