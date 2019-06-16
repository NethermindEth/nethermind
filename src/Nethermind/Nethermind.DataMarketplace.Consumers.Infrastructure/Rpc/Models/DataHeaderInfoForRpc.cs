using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Domain;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class DataHeaderInfoForRpc
    {
        public Keccak Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }


        public DataHeaderInfoForRpc()
        {
        }

        public DataHeaderInfoForRpc(DataHeaderInfo dataHeader)
        {
            Id = dataHeader.Id;
            Name = dataHeader.Name;
            Description = dataHeader.Description;
        }
    }
}