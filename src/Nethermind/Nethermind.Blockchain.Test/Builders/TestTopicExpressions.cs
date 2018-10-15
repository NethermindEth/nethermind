using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Test.Builders
{
    public class TestTopicExpressions
    {
        public static TopicExpression Specific(Keccak keccak) => new SpecificTopic(keccak);
        public static TopicExpression Any => new AnyTopic();
        public static TopicExpression Or(IEnumerable<TopicExpression> topics) => new OrExpression(topics.ToArray());
    }
}