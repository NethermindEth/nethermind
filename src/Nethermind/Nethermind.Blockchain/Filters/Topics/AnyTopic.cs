using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class AnyTopic : TopicExpression
    {
        public override bool Accepts(Keccak topic)
        {
            return true;
        }
    }
}