// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class TrieNodeResolverWithReadFlagsTests
{
    [Test]
    public void LoadRlp_shouldPassTheFlag()
    {
        ReadFlags theFlags = ReadFlags.HintCacheMiss;
        TestMemDb memDb = new();
        ITrieStore trieStore = new TrieStore(memDb, LimboLogs.Instance);
        TrieNodeResolverWithReadFlags resolver = new(trieStore, theFlags);

        Hash256 theKeccak = TestItem.KeccakA;
        memDb[theKeccak.Bytes] = TestItem.KeccakA.BytesToArray();
        resolver.LoadRlp(theKeccak);

        memDb.KeyWasReadWithFlags(theKeccak.BytesToArray(), theFlags);
    }

    [Test]
    public void LoadRlp_combine_passed_fleag()
    {
        ReadFlags theFlags = ReadFlags.HintCacheMiss;
        TestMemDb memDb = new();
        ITrieStore trieStore = new TrieStore(memDb, LimboLogs.Instance);
        TrieNodeResolverWithReadFlags resolver = new(trieStore, theFlags);

        Hash256 theKeccak = TestItem.KeccakA;
        memDb[theKeccak.Bytes] = TestItem.KeccakA.BytesToArray();
        resolver.LoadRlp(theKeccak, ReadFlags.HintReadAhead);

        memDb.KeyWasReadWithFlags(theKeccak.BytesToArray(), theFlags | ReadFlags.HintReadAhead);
    }
}
