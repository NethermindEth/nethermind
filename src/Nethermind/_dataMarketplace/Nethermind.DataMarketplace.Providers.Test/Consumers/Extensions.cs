using System.Linq;
using FluentAssertions;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Providers.Test.Consumers
{
    internal static class Extensions
    {
        public static void ShouldDeliverReceiptWithinRange(this TestConsumerNode node, uint number, uint from, uint to)
        {
            node.Receipts.Should().NotBeEmpty();
            var receipt = node.Receipts.Single(r => r.Number == number);
            receipt.Request.UnitsRange.Should().Be(new UnitsRange(from, to));
        }
        
        public static void ShouldNotDeliverReceipt(this TestConsumerNode node)
        {
            node.Receipts.Should().BeEmpty();
        }
    }
}