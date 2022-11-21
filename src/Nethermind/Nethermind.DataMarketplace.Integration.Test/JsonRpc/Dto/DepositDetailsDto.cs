// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Integration.Test.JsonRpc.Dto
{
    public class DepositDetailsDto
    {
        public DepositDto Deposit { get; set; }
        public bool Confirmed { get; set; }
        public uint StartTimestamp { get; set; }
        public uint SessionTimestamp { get; set; }
        public string TransactionHash { get; set; }
        public DataAssetDto DataAsset { get; set; }
        public DataRequestDto DataRequest { get; set; }
        public string[] Args { get; set; }
        public bool StreamEnabled { get; set; }
        public uint ConsumedUnitsFromProvider { get; set; }
        public long ConsumedUnitsFromConsumer { get; set; }
        public long StartUnits { get; set; }
        public long CurrentUnits { get; set; }
        public uint UnpaidUnits { get; set; }
        public uint PaidUnits { get; set; }
        public string DataAvailability { get; set; }
        public bool RefundClaimed { get; set; }
        public string ClaimedRefundTransactionHash { get; set; }
    }
}
