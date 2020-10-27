using Nethermind.Core;

namespace Nethermind.Blockchain.Filters.Topics
{
    public abstract class TopicsFilter
    {
        public abstract bool Accepts(LogEntry entry);

        public abstract bool Accepts(ref LogEntryStructRef entry);

        public abstract bool Matches(Bloom bloom);

        public abstract bool Matches(ref BloomStructRef bloom);
    }
}
