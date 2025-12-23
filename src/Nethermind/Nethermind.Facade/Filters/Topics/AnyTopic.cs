// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db.LogIndex; // TODO: get rid of LogIndex usage

namespace Nethermind.Blockchain.Filters.Topics
{
    public class AnyTopic : TopicExpression
    {
        public static readonly AnyTopic Instance = new();

        private AnyTopic() { }

        public override bool Accepts(Hash256 topic) => true;
        public override bool Accepts(ref Hash256StructRef topic) => true;

        public override bool Matches(Bloom bloom) => true;
        public override bool Matches(ref BloomStructRef bloom) => true;

        public override bool AcceptsAnyBlock => true;

        public override IEnumerable<Hash256> Topics => [];

        public override IList<FullLogPosition>? FilterPositions(IDictionary<Hash256, IList<FullLogPosition>> byTopic) => null;

        public override string ToString() => "null";
    }
}
