// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
