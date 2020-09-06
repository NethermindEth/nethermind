/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

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
