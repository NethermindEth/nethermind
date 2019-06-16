using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class ConsumerSessionForRpc : SessionForRpc
    {
        public uint ConsumedUnitsFromProvider { get; set; }
        public string DataAvailability { get; set; }
        public bool StreamEnabled { get; set; }
        public string[] Subscriptions { get; set; }

        public ConsumerSessionForRpc()
        {
        }
        
        public ConsumerSessionForRpc(ConsumerSession session) : base(session)
        {
            ConsumedUnitsFromProvider = session.ConsumedUnitsFromProvider;
            DataAvailability = session.DataAvailability.ToString().ToLowerInvariant();
            StreamEnabled = session.StreamEnabled;
            Subscriptions = session.Subscriptions;
        }
    }
}