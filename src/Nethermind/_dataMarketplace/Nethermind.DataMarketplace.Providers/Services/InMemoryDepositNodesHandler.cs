using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Services
{
    internal class InMemoryDepositNodesHandler : IDepositNodesHandler
    {
            private readonly ConcurrentStack<DataDeliveryReceiptDetails> _receipts;
            private DataDeliveryReceiptDetails? _latestReceipt;
            private DataDeliveryReceiptDetails? _latestMergedReceipt;
            private PaymentClaim? _latestPaymentClaim;
            private int _isHandling;
            private long _consumedUnits;
            private long _unpaidUnits;
            private long _unmergedUnits;
            private long _unclaimedUnits;
            private long _graceUnits;
            private int _currentLastClaimRetry;
            private long _sentUnits;
            private long _latestReceiptRequestNumber;
            public Keccak DepositId { get; }
            public Address Consumer { get; }
            public DataAssetUnitType UnitType { get; }
            public uint VerificationTimestamp { get; }
            public long PurchasedUnits { get; }
            public UInt256 UnitPrice { get; }
            public uint ConsumedUnits => (uint) _consumedUnits;
            public uint UnpaidUnits => (uint) _unpaidUnits;
            public uint UnmergedUnits => (uint) _unmergedUnits;
            public uint UnclaimedUnits => (uint) _unclaimedUnits;
            public uint GraceUnits => (uint) _graceUnits;
            public uint ExpiryTime { get; }
            public PaymentClaim? LatestPaymentClaim => _latestPaymentClaim;
            public IEnumerable<DataDeliveryReceiptDetails> Receipts => _receipts;
            public int CurrentLastClaimRetry => _currentLastClaimRetry;

            public bool HasClaimedAllUnits => LatestReceipt?.IsClaimed == true &&
                                              LatestReceipt?.Request.UnitsRange.To == PurchasedUnits - 1 ||
                                              LatestMergedReceipt?.IsClaimed == true &&
                                              LatestMergedReceipt?.Request.UnitsRange.To == PurchasedUnits - 1;

            public bool HasSentAllReceipts => LatestReceipt?.Request.UnitsRange.To == PurchasedUnits - 1
                                              || HasSentLastMergedReceipt;

            public bool HasSentLastMergedReceipt => LatestMergedReceipt?.Request.UnitsRange.To == PurchasedUnits - 1;

            public DataDeliveryReceiptDetails? LatestReceipt => _latestReceipt;
            public DataDeliveryReceiptDetails? LatestMergedReceipt => _latestMergedReceipt;

            public bool ConsumedAll => ConsumedUnits >= PurchasedUnits;

            public InMemoryDepositNodesHandler(Keccak depositId, Address consumer, DataAssetUnitType unitType,
                uint verificationTimestamp, uint purchasedUnits, UInt256 unitPrice, uint consumedUnits,
                uint unpaidUnits, uint unmergedUnits, uint unclaimedUnits, uint graceUnits, uint expiryTime,
                PaymentClaim latestPaymentClaim, IEnumerable<DataDeliveryReceiptDetails> receipts,
                uint latestReceiptRequestNumber)
            {
                DepositId = depositId;
                Consumer = consumer;
                UnitType = unitType;
                VerificationTimestamp = verificationTimestamp;
                PurchasedUnits = purchasedUnits;
                UnitPrice = unitPrice;
                _consumedUnits = consumedUnits;
                _unpaidUnits = unpaidUnits;
                _unmergedUnits = unmergedUnits;
                _unclaimedUnits = unclaimedUnits;
                _graceUnits = graceUnits;
                ExpiryTime = expiryTime;
                _latestPaymentClaim = latestPaymentClaim;
                _receipts = new ConcurrentStack<DataDeliveryReceiptDetails>(
                    receipts ?? Enumerable.Empty<DataDeliveryReceiptDetails>());
                _latestReceiptRequestNumber = latestReceiptRequestNumber;
                var latestReceipt = _receipts.Where(r => !r.IsMerged).OrderBy(r => r.Number).LastOrDefault();
                var latestMergedReceipt = _receipts.Where(r => r.IsMerged).OrderBy(r => r.Number).LastOrDefault();
                SetLatestReceipt(latestReceipt);
                SetLatestReceipt(latestMergedReceipt);
            }

            public bool IsExpired(uint timestamp) => timestamp >= ExpiryTime;

            public bool TryHandle() => Interlocked.Exchange(ref _isHandling, 1) == 0;
            public void FinishHandling() => Interlocked.Exchange(ref _isHandling, 0);
            public bool TryIncreaseSentUnits() => Interlocked.Increment(ref _sentUnits) <= PurchasedUnits;

            public void AddReceipt(DataDeliveryReceiptDetails receipt)
            {
                _receipts.Push(receipt);
                SetLatestReceipt(receipt);
            }

            private void SetLatestReceipt(DataDeliveryReceiptDetails receipt)
            {
                if (receipt is null)
                {
                    return;
                }
                
                if (receipt.IsMerged)
                {
                    Interlocked.Exchange(ref _latestMergedReceipt, receipt);
                    Interlocked.Exchange(ref _latestReceipt, receipt);
                    return;
                }
                
                Interlocked.Exchange(ref _latestReceipt, receipt);
            }

            public uint GetNextReceiptRequestNumber() => (uint)Interlocked.Increment(ref _latestReceiptRequestNumber);

            public void ClearReceipts() => _receipts.Clear();

            public void SetLatestPaymentClaim(PaymentClaim claim) =>
                Interlocked.Exchange(ref _latestPaymentClaim, claim);

            public void IncrementConsumedUnits() => Interlocked.Increment(ref _consumedUnits);
            public void SetConsumedUnits(uint units) => Interlocked.Exchange(ref _consumedUnits, units);
            public void IncrementUnpaidUnits() => Interlocked.Increment(ref _unpaidUnits);
            public void SetUnpaidUnits(uint units) => Interlocked.Exchange(ref _unpaidUnits, units);
            public void SubtractUnpaidUnits(uint units) => Interlocked.Add(ref _unpaidUnits, -1 * units);
            public void AddUnpaidUnits(uint units) => Interlocked.Add(ref _unpaidUnits, units);
            public void IncrementUnmergedUnits() => Interlocked.Increment(ref _unmergedUnits);
            public void SetUnmergedUnits(uint units) => Interlocked.Exchange(ref _unmergedUnits, units);
            public void SubtractUnmergedUnits(uint units) => Interlocked.Add(ref _unmergedUnits, -1 * units);
            public void AddUnmergedUnits(uint units) => Interlocked.Add(ref _unmergedUnits, units);
            public void IncrementUnclaimedUnits() => Interlocked.Increment(ref _unclaimedUnits);
            public void SetUnclaimedUnits(uint units) => Interlocked.Exchange(ref _unclaimedUnits, units);
            public void SubtractUnclaimedUnits(uint units) => Interlocked.Add(ref _unclaimedUnits, -1 * units);
            public void AddUnclaimedUnits(uint units) => Interlocked.Add(ref _unclaimedUnits, units);
            public void IncrementLastClaimRetries() => Interlocked.Increment(ref _currentLastClaimRetry);
            public void AddGraceUnits(uint units) => Interlocked.Add(ref _graceUnits, units);
    }
}