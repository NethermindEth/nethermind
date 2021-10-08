//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public interface INdmPeer : IDisposable
    {
        PublicKey NodeId { get; }
        Address? ConsumerAddress { get; }
        Address? ProviderAddress { get; }
        bool IsConsumer { get; }
        bool IsProvider { get; }
        void ChangeConsumerAddress(Address address);
        void ChangeProviderAddress(Address address);
        void ChangeHostConsumerAddress(Address address);
        void ChangeHostProviderAddress(Address address);
        void SendConsumerAddressChanged(Address consumer);

        Task<DataRequestResult> SendDataRequestAsync(DataRequest dataRequest, uint consumedUnits,
            CancellationToken? token = null);

        void SendFinishSession(Keccak depositId);
        void SendEnableDataStream(Keccak depositId, string client, string?[] args);
        void SendDisableDataStream(Keccak depositId, string client);
        void SendDataDeliveryReceipt(Keccak depositId, DataDeliveryReceipt receipt);
        Task<FaucetResponse> SendRequestEthAsync(Address address, UInt256 value, CancellationToken? token = null);
        void SendRequestDepositApproval(Keccak assetId, Address consumer, string kyc);

        Task<IReadOnlyList<DepositApproval>> SendGetDepositApprovals(Keccak? dataAssetId = null,
            bool onlyPending = false, CancellationToken? token = null);
    }
}