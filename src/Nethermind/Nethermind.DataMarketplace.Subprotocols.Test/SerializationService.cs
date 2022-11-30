// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
