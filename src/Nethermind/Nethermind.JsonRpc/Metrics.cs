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

using System.ComponentModel;
using System.Threading;

namespace Nethermind.JsonRpc
{
    public static class Metrics
    {
        [Description("Total number of JSON RPC requests received by the node.")]
        public static long JsonRpcRequests { get; set; }
        
        [Description("Number of JSON RPC requests that failed JSON deserialization.")]
        public static long JsonRpcRequestDeserializationFailures { get; set; }
        
        [Description("Number of JSON RPC requests that were invalid.")]
        public static long JsonRpcInvalidRequests { get; set; }
        
        [Description("Number of JSON RPC requests processed with errors.")]
        public static long JsonRpcErrors { get; set; }
        
        [Description("Number of JSON RPC requests processed succesfully.")]
        public static long JsonRpcSuccesses { get; set; }

        [Description("Number of JSON RPC bytes sent.")]
        public static long JsonRpcBytesSent => JsonRpcBytesSentHttp + JsonRpcBytesSentWebSockets + JsonRpcBytesSentIpc;
        
        [Description("Number of JSON RPC bytes sent through http.")]
        public static long JsonRpcBytesSentHttp;
        
        [Description("Number of JSON RPC bytes sent through web sockets.")]
        public static long JsonRpcBytesSentWebSockets;

        [Description("Number of JSON RPC bytes sent through IPC.")]
        public static long JsonRpcBytesSentIpc;

        [Description("Number of JSON RPC bytes received.")]
        public static long JsonRpcBytesReceived => JsonRpcBytesReceivedHttp + JsonRpcBytesReceivedWebSockets + JsonRpcBytesReceivedIpc;
        
        [Description("Number of JSON RPC bytes received through http.")]
        public static long JsonRpcBytesReceivedHttp;
        
        [Description("Number of JSON RPC bytes received through web sockets.")]
        public static long JsonRpcBytesReceivedWebSockets;

        [Description("Number of JSON RPC bytes received through IPC.")]
        public static long JsonRpcBytesReceivedIpc;
    }
}
