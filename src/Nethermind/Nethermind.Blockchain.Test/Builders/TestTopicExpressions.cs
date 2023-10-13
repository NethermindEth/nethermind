// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Test.Builders
{
    public class TestTopicExpressions
    {
        public static TopicExpression Specific(Commitment commitment) => new SpecificTopic(commitment);
        public static TopicExpression Any => AnyTopic.Instance;
        public static TopicExpression Or(params TopicExpression[] topics) => new OrExpression(topics);
        public static TopicExpression Or(params Commitment[] topics) => Or(topics.Select(Specific).ToArray());
    }
}
