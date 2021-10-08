using System.Collections.Generic;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;

namespace Nethermind.DataMarketplace.Providers.Test.Consumers
{
    internal class TestConsumerNode
    {
        private readonly List<DataDeliveryReceiptDetails> _receipts = new List<DataDeliveryReceiptDetails>();
        public int Id { get; }
        public ConsumerNode Node { get; }
        public IEnumerable<DataDeliveryReceiptDetails> Receipts => _receipts;
        public IEnumerable<ProviderSession> Sessions => Node.Sessions;
        

        public TestConsumerNode(int id, ConsumerNode node)
        {
            Id = id;
            Node = node;
        }

        public void AddSession(ProviderSession session) => Node.AddSession(session);

        public void AddReceipts(params DataDeliveryReceiptDetails[] receipts)
        {
            if (receipts is null)
            {
                return;
            }

            _receipts.AddRange(receipts);
        }
    }
}