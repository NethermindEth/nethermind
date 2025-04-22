// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

global using InternalStore =
    System.Collections.Concurrent.ConcurrentDictionary<byte[], Nethermind.Verkle.Tree.TreeNodes.InternalNode?>;
global using LeafStore = System.Collections.Concurrent.ConcurrentDictionary<byte[], byte[]?>;
global using LeafStoreSorted = Nethermind.Core.Collections.DictionarySortedSet<byte[], byte[]>;
global using VerkleUtils = Nethermind.Verkle.Tree.Utils.VerkleUtils;
global using VerkleNodeType = Nethermind.Verkle.Tree.TreeNodes.VerkleNodeType;
global using InternalStoreInterface =
    System.Collections.Generic.IDictionary<byte[], Nethermind.Verkle.Tree.TreeNodes.InternalNode?>;
global using LeafStoreInterface = System.Collections.Generic.IDictionary<byte[], byte[]?>;
global using LeafEnumerator =
    System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<byte[], byte[]>>;
global using Committer = Nethermind.Core.Verkle.Committer;
