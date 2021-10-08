using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public class DepositNodesHandlerFactory : IDepositNodesHandlerFactory
    {
        public IDepositNodesHandler CreateInMemory(Keccak depositId, Address consumer, DataAssetUnitType unitType,
            uint verificationTimestamp, uint purchasedUnits, UInt256 unitPrice, uint consumedUnits, uint unpaidUnits,
            uint unmergedUnits, uint unclaimedUnits, uint graceUnits, uint expiryTime, PaymentClaim latestPaymentClaim,
            IEnumerable<DataDeliveryReceiptDetails> latestReceipts, uint latestReceiptRequestNumber)
            => new InMemoryDepositNodesHandler(depositId, consumer, unitType, verificationTimestamp,
                purchasedUnits, unitPrice, consumedUnits, unpaidUnits, unmergedUnits, unclaimedUnits, graceUnits,
                expiryTime, latestPaymentClaim, latestReceipts, latestReceiptRequestNumber);
    }
}