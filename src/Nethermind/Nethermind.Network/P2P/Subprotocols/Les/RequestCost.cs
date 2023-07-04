// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class RequestCostItem
    {
        public int MessageCode;
        public int BaseCost;
        public int RequestCost;
        public RequestCostItem(int messageCode, int baseCost, int requestCost)
        {
            MessageCode = messageCode;
            BaseCost = baseCost;
            RequestCost = requestCost;
        }
    }
}
