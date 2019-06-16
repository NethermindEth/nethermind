using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Consumers.Domain
{
    public class DataHeaderInfo
    {
        public Keccak Id { get; }
        public string Name { get; }
        public string Description { get; }

        public DataHeaderInfo(Keccak id, string name, string description)
        {
            Id = id;
            Name = name;
            Description = description;
        }
    }
}