// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

global using InternalStore = Nethermind.Core.Collections.SpanConcurrentDictionary<byte, Nethermind.Verkle.Tree.Nodes.InternalNode?>;
global using LeafStore = Nethermind.Core.Collections.SpanConcurrentDictionary<byte, byte[]?>;
global using LeafStoreSorted = System.Collections.Generic.SortedDictionary<byte[], byte[]?>;
global using VerkleUtils = Nethermind.Verkle.Tree.Utils.VerkleUtils;
global using VerkleNodeType = Nethermind.Verkle.Tree.Nodes.VerkleNodeType;
global using InternalStoreInterface = System.Collections.Generic.IDictionary<byte[], Nethermind.Verkle.Tree.Nodes.InternalNode?>;
global using LeafStoreInterface = System.Collections.Generic.IDictionary<byte[], byte[]?>;
global using LeafEnumerator = System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<byte[],byte[]>>;
global using Committer = Nethermind.Core.Verkle.Committer;
