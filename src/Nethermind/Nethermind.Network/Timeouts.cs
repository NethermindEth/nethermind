//  Copyright (c) 2021 Demerzel Solutions Limited
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

namespace Nethermind.Network
{
    public static class Timeouts
    {
        public static readonly TimeSpan InitialConnection = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan TcpClose = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan Eth = Synchronization.Timeouts.Eth;
        public static readonly TimeSpan P2PPing = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan P2PHello = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan Eth62Status = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan Les3Status = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan NdmHi = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan NdmDeliveryReceipt = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan NdmDepositApproval = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan NdmEthRequest = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan NdmDataRequestResult = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan Handshake = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan Disconnection = TimeSpan.FromSeconds(1);
    }
}
