// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

global using BranchStore = System.Collections.Concurrent.ConcurrentDictionary<byte[], Nethermind.Verkle.Tree.Nodes.InternalNode?>;
global using LeafStore = System.Collections.Concurrent.ConcurrentDictionary<byte[], byte[]?>;
global using StemStore = System.Collections.Concurrent.ConcurrentDictionary<byte[], Nethermind.Verkle.Tree.Nodes.SuffixTree?>;
global using VerkleUtils = Nethermind.Verkle.Utils.VerkleUtils;
global using NodeType = Nethermind.Verkle.Tree.Nodes.NodeType;

