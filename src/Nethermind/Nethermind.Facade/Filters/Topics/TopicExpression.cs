// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Filters.Topics
{
    public abstract class TopicExpression
    {
        public abstract bool Accepts(Keccak topic);

        public abstract bool Accepts(ref KeccakStructRef topic);

        public abstract bool Matches(Bloom bloom);

        public abstract bool Matches(ref BloomStructRef bloom);

        public abstract IEnumerable<Keccak> OrTopicExpression { get; }
    }
}
