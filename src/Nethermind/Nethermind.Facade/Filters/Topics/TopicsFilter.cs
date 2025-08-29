// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public abstract class TopicsFilter
    {
        public abstract bool Accepts(LogEntry entry);

        public abstract bool Accepts(ref LogEntryStructRef entry);

        public abstract bool Matches(Bloom bloom);

        public abstract bool Matches(ref BloomStructRef bloom);

        public abstract bool AcceptsAnyBlock { get; }

        public abstract IEnumerable<Hash256> Topics { get; }

        public abstract List<int> FilterBlockNumbers(IReadOnlyDictionary<Hash256, List<int>> byTopic);
    }
}
