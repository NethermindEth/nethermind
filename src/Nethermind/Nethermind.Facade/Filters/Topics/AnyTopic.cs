// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Filters.Topics
{
    public class AnyTopic : TopicExpression
    {
        public static readonly AnyTopic Instance = new();

        private AnyTopic() { }

        public override bool Accepts(Hash256 topic) => true;
        public override bool Accepts(ref Hash256StructRef topic) => true;

        public override bool Matches(Bloom bloom) => true;
        public override bool Matches(ref BloomStructRef bloom) => true;

        public override IEnumerable<Hash256> OrTopicExpression => Enumerable.Empty<Hash256>();

        public override string ToString() => "null";
    }
}
