// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class AnyTopic : TopicExpression
    {
        public static readonly AnyTopic Instance = new();
        public static IEnumerable<int> Any = [-1];

        private AnyTopic() { }


        public override IEnumerable<int> GetBlockNumbersFrom(LogIndexStorage logIndexStorage)
        {
            return Any;
        }


    public override bool Accepts(Hash256 topic) => true;
    public override bool Accepts(ref Hash256StructRef topic) => true;

    public override bool Matches(Bloom bloom) => true;
    public override bool Matches(ref BloomStructRef bloom) => true;

    public override string ToString() => "null";
    }
}
