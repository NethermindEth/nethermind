// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public abstract class TopicExpression
    {
        public abstract bool Accepts(Hash256 topic);

        public abstract bool Accepts(ref Hash256StructRef topic);

        public abstract bool Matches(Bloom bloom);

        public abstract bool Matches(ref BloomStructRef bloom);

        public abstract bool AcceptsAnyBlock { get; }

        public abstract IEnumerable<Hash256> Topics { get; }

        public abstract HashSet<int>? FilterBlockNumbers(IReadOnlyDictionary<Hash256, List<int>> byTopic);
    }
}
