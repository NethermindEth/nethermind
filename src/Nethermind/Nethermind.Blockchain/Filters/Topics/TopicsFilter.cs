using Nethermind.Core;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class TopicsFilter
    {
        private readonly TopicExpression[] _expressions;

        public TopicsFilter(TopicExpression[] expressions)
        {
            _expressions = expressions;
        }

        public bool Accepts(LogEntry entry)
        {
            for (int i = 0; i < _expressions.Length; i++)
            {
                if (_expressions.Length > entry.Topics.Length)
                {
                    return false;
                }

                if (!_expressions[i].Accepts(entry.Topics[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}