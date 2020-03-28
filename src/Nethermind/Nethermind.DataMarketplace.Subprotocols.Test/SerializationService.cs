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

using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;

namespace Nethermind.DataMarketplace.Subprotocols.Test
{
    public static class SerializationService
    {
        public static IMessageSerializationService WithAllSerializers
        {
            get
            {
                InitDecoders();
                var service = new MessageSerializationService();
                service.Register(typeof(HiMessage).Assembly);
                return service;
            }
        }

        private static void InitDecoders()
        {
            DataAssetDecoder.Init();
            DataAssetRulesDecoder.Init();
            DataAssetRuleDecoder.Init();
            DataAssetProviderDecoder.Init();
            DataDeliveryReceiptDecoder.Init();
            DataRequestDecoder.Init();
            EarlyRefundTicketDecoder.Init();
            FaucetResponseDecoder.Init();
            FaucetRequestDetailsDecoder.Init();
            DataDeliveryReceiptDecoder.Init();
            DataDeliveryReceiptRequestDecoder.Init();
            UnitsRangeDecoder.Init();
            SessionDecoder.Init();
            DepositApprovalDecoder.Init();
            DataDeliveryReceiptToMergeDecoder.Init();
        }
    }
}