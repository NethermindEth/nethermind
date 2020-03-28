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

using System.Collections.Generic;
using System.Reflection;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class NdmMessageCode
    {
        public const int Hi = 0x00;
        public const int GetDataAssets = 0x01;
        public const int DataAssets = 0x02;
        public const int DataAsset = 0x03;
        public const int DataAssetStateChanged = 0x4;
        public const int DataAssetRemoved = 0x05;
        public const int DataAssetData = 0x06;
        public const int InvalidData = 0x07;
        public const int DataRequest = 0x08;
        public const int DataRequestResult = 0x09;
        public const int SessionStarted = 0x0A;
        public const int EnableDataStream = 0x0B;
        public const int DisableDataStream = 0x0C;
        public const int DataStreamEnabled = 0x0D;
        public const int DataStreamDisabled = 0x0E;
        public const int DataAvailability = 0x0F;
        public const int RequestDataDeliveryReceipt = 0x10;
        public const int DataDeliveryReceipt = 0x11;
        public const int EarlyRefundTicket = 0x12;
        public const int FinishSession = 0x13;
        public const int SessionFinished = 0x14;
        public const int RequestDepositApproval = 0x15;
        public const int DepositApprovalConfirmed = 0x16;
        public const int DepositApprovalRejected = 0x17;
        public const int GetDepositApprovals = 0x18;
        public const int DepositApprovals = 0x19;
        public const int ConsumerAddressChanged = 0x1A;
        public const int ProviderAddressChanged = 0x1B;
        public const int RequestEth = 0x1C;
        public const int EthRequested = 0x1D;
        public const int GraceUnitsExceeded = 0x1E;

        public static bool IsProviderOnly(int messageCode)
        {
            return messageCode == ConsumerAddressChanged
                   || messageCode == GetDataAssets
                   || messageCode == GetDepositApprovals
                   || messageCode == DataDeliveryReceipt
                   || messageCode == DataRequest
                   || messageCode == EnableDataStream
                   || messageCode == DisableDataStream
                   || messageCode == FinishSession
                   || messageCode == RequestDepositApproval
                   || messageCode == RequestEth;
        }

        public static bool IsRequestResponse(int messageCode)
        {
            return messageCode == DataRequestResult
                   || messageCode == EthRequested;
        }
        
        private static Dictionary<int, string>? _descriptions;
        
        public static string? GetDescription(int messageCode)
        {
            if (_descriptions == null)
            {
                _descriptions = new Dictionary<int, string>();
                foreach (FieldInfo fieldInfo in typeof(NdmMessageCode).GetFields(BindingFlags.Static | BindingFlags.Public))
                {
                    _descriptions.Add((int)(fieldInfo.GetValue(null) ?? 0), fieldInfo.Name);
                }
            }

            bool success = _descriptions.TryGetValue(messageCode, out string? description);
            return success ? description : "unknown";
        }
    }
}