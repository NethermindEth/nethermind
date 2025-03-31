// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie;

namespace Nethermind.Core.Test.Builders
{
    public partial class Build
    {
        public TrieBuilder Trie(IKeyValueStoreWithBatching db) => new(new NodeStorage(db));
        public TrieBuilder Trie(INodeStorage db) => new(db);
    }
}
