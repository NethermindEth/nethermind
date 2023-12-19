// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Filters.Topics;

namespace Nethermind.Blockchain.Test.Builders
{
    public class TestTopicExpressions
    {
        public static SpecificTopic Specific(Hash256 keccak) => new SpecificTopic(keccak);
        public static TopicExpression Any => AnyTopic.Instance;
        public static TopicExpression Or(params SpecificTopic[] topics) => new OrExpression(topics);
        public static TopicExpression Or(params Hash256[] topics) => Or(topics.Select(Specific).ToArray());
    }
}
