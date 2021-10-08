using System.Linq;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;
using Nethermind.DataMarketplace.Providers.Domain;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models
{
    internal class ProviderSessionForRpc : SessionForRpc
    {
        public uint GraceUnits { get; set; }
        public string DataAvailability { get; set; }
        public SessionClientForRpc[] Clients { get; set; }

        public ProviderSessionForRpc(ProviderSession session) : base(session)
        {
            GraceUnits = session.GraceUnits;
            DataAvailability = session.DataAvailability.ToString().ToLowerInvariant();
            Clients = session.Clients.Select(c => new SessionClientForRpc(c)).ToArray();
        }
    }
}