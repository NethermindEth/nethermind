// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.RocksDbExtractor.ProviderDecoders.Domain
{
    public class Consumer
    {
        public Keccak DepositId { get; }
        public uint VerificationTimestamp { get; }
        public DataRequest DataRequest { get; }
        public DataAsset DataAsset { get; }
        public bool HasAvailableUnits { get; }

        public Consumer(Keccak depositId, uint verificationTimestamp, DataRequest dataRequest,
            DataAsset dataAsset, bool hasAvailableUnits = true)
        {
            DepositId = depositId;
            VerificationTimestamp = verificationTimestamp;
            DataRequest = dataRequest;
            DataAsset = dataAsset;
            HasAvailableUnits = hasAvailableUnits;
        }
    }
}
