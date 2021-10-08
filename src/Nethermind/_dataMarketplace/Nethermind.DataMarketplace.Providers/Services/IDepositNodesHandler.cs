using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public interface IDepositNodesHandler
    {
        Keccak DepositId { get; }
        Address Consumer { get; }
        DataAssetUnitType UnitType { get; }
        uint VerificationTimestamp { get; }
        long PurchasedUnits { get; }
        UInt256 UnitPrice { get; }
        uint ConsumedUnits { get; }
        uint UnpaidUnits { get; }
        uint UnmergedUnits { get; }
        uint UnclaimedUnits { get; }
        uint GraceUnits { get; }
        uint ExpiryTime { get; }
        PaymentClaim? LatestPaymentClaim { get; }
        IEnumerable<DataDeliveryReceiptDetails> Receipts { get; }
        int CurrentLastClaimRetry { get; }
        bool HasClaimedAllUnits { get; }
        bool HasSentAllReceipts { get; }
        bool HasSentLastMergedReceipt { get; }
        DataDeliveryReceiptDetails? LatestReceipt { get; }
        DataDeliveryReceiptDetails? LatestMergedReceipt { get; }
        bool ConsumedAll { get; }
        bool IsExpired(uint timestamp);
        bool TryHandle();
        void FinishHandling();
        bool TryIncreaseSentUnits();
        void AddReceipt(DataDeliveryReceiptDetails receipt);
        uint GetNextReceiptRequestNumber();
        void ClearReceipts();
        void SetLatestPaymentClaim(PaymentClaim claim);
        void IncrementConsumedUnits();
        void SetConsumedUnits(uint units);
        void IncrementUnpaidUnits();
        void SetUnpaidUnits(uint units);
        void SubtractUnpaidUnits(uint units);
        void AddUnpaidUnits(uint units);
        void IncrementUnmergedUnits();
        void SetUnmergedUnits(uint units);
        void SubtractUnmergedUnits(uint units);
        void AddUnmergedUnits(uint units);
        void IncrementUnclaimedUnits();
        void SetUnclaimedUnits(uint units);
        void SubtractUnclaimedUnits(uint units);
        void AddUnclaimedUnits(uint units);
        void IncrementLastClaimRetries();
        void AddGraceUnits(uint units);
    }
}