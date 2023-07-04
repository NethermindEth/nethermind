// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Overseer.Test.JsonRpc.Dto
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
        public long ProviderTotalUnits { get; set; }
        public long ConsumerTotalUnits { get; set; }
        public long StartUnits { get; set; }
        public long CurrentUnits { get; set; }
        public long UnpaidUnits { get; set; }
        public long PaidUnits { get; set; }
        public string DataAvailability { get; set; }
    }
}
