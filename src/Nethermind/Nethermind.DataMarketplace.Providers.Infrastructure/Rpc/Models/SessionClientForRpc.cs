using Nethermind.DataMarketplace.Providers.Domain;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models
{
    internal class SessionClientForRpc
    {
        public string? Id { get; set; }
        public bool StreamEnabled { get; set; }
        public string[]? Args { get; set; }

        public SessionClientForRpc()
        {
        }

        public SessionClientForRpc(SessionClient session)
        {
            Id = session.Id;
            StreamEnabled = session.StreamEnabled;
            Args = session.Args;
        }
    }
}