using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class OrExpression : TopicExpression
    {
        private readonly TopicExpression[] _subexpression;

        public OrExpression(TopicExpression[] subexpression)
        {
            _subexpression = subexpression;
        }

        public override bool Accepts(Keccak topic)
        {
            for (int i = 0; i < _subexpression.Length; i++)
            {
                if (_subexpression[i].Accepts(topic))
                {
                    return true;
                }
            }

            return false;
        }
    }
}