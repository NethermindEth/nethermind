using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public abstract class TopicExpression
    {
        public abstract bool Accepts(Keccak topic);
    }
}