// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Test.Builders
{
    public class TestTopicExpressions
    {
        public static TopicExpression Specific(Keccak keccak) => new SpecificTopic(keccak);
        public static TopicExpression Any => AnyTopic.Instance;
        public static TopicExpression Or(params TopicExpression[] topics) => new OrExpression(topics);
        public static TopicExpression Or(params Keccak[] topics) => Or(topics.Select(Specific).ToArray());
    }
}
