using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class SpecificTopic : TopicExpression
    {
        private readonly Keccak _topic;

        public SpecificTopic(Keccak topic)
        {
            _topic = topic;
        }

        public override bool Accepts(Keccak topic)
        {
            return topic == _topic;
        }
    }
}