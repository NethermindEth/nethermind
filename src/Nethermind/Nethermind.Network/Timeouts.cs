// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network
{
    public static class Timeouts
    {
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
