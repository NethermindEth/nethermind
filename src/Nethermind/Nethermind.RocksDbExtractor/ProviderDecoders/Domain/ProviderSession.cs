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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.RocksDbExtractor.ProviderDecoders.Domain
{
    public class ProviderSession : Session
    {
        private long _graceUnits;
        private int _dataAvailability;

        public DataAvailability DataAvailability
        {
            get => (DataAvailability)_dataAvailability;
            private set => _dataAvailability = (int)value;
        }

        public uint GraceUnits
        {
            get => (uint)_graceUnits;
            private set => _graceUnits = value;
        }

        public ProviderSession(Keccak id, Keccak depositId, Keccak dataAssetId, Address consumerAddress,
            PublicKey consumerNodeId, Address providerAddress, PublicKey providerNodeId, SessionState state,
            uint startUnitsFromConsumer, uint startUnitsFromProvider, ulong startTimestamp = 0,
            ulong finishTimestamp = 0, uint consumedUnits = 0, uint unpaidUnits = 0, uint paidUnits = 0,
            uint settledUnits = 0, uint graceUnits = 0, DataAvailability dataAvailability = DataAvailability.Unknown) :
            base(id, depositId, dataAssetId, consumerAddress, consumerNodeId, providerAddress, providerNodeId, state,
                startUnitsFromConsumer, startUnitsFromProvider, startTimestamp, finishTimestamp, consumedUnits,
                unpaidUnits, paidUnits, settledUnits)
        {
            _graceUnits = graceUnits;
            _dataAvailability = (int)dataAvailability;
        }
    }
}
