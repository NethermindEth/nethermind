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
//

using System;
using System.Linq;

using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class JsonRpcUrl : ICloneable
    {
        private const string HttpEndpointValue = "http";
        private const string WebSocketEndpointValue = "ws";

        public JsonRpcUrl(string scheme, string host, int port, RpcEndpoint rpcEndpoint, string[] enabledModules)
        {
            Scheme = scheme;
            Host = host;
            Port = port;
            RpcEndpoint = rpcEndpoint;
            EnabledModules = enabledModules;
        }

        public static JsonRpcUrl Parse(string packedUrlValue)
        {
            //Ensure packaged url value specified
            if (packedUrlValue == null)
                throw new ArgumentNullException(nameof(packedUrlValue));

            //Unpack parts
            string[] parts = packedUrlValue.Split('|');
            if (parts.Length != 3)
                throw new FormatException("Packed url value must contain 3 parts delimited by '|'");

            //Parse url part
            string url = parts[0];
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                throw new FormatException("First part must be a valid url");

            //Parse endpoint part
            string[] endpointValues = parts[1].Split(',');
            if (endpointValues.Length == 0)
                throw new FormatException("Second part must contain at least one valid endpoint value delimited by ','");

            //Parse endpoint values
            RpcEndpoint endpoint = RpcEndpoint.None;
            foreach (string endpointValue in endpointValues)
            {
                switch(endpointValue)
                {
                    case HttpEndpointValue:
                        endpoint |= RpcEndpoint.Http;
                        break;
                    case WebSocketEndpointValue:
                        endpoint |= RpcEndpoint.WebSocket;
                        break;
                    default:
                        break;
                }
            }

            //Ensure at least one valid endpoint value was observed
            if (endpoint == RpcEndpoint.None)
                throw new FormatException($"Second part must contain at least one valid endpoint value: {HttpEndpointValue}, {WebSocketEndpointValue}");

            //Parse enabled modules part
            string[] enabledModules = parts[2].Split(',');
            if (enabledModules.Length == 0)
                throw new FormatException("Third part must contain at least one valid endpoint value delimited by ','");

            //Ensure all enabled modules are valid
            bool modulesValid = enabledModules.All(x => ModuleType.AllBuiltInModules.Contains(x));
            if (!modulesValid)
                throw new FormatException($"Fourth part must contain at least one valid module: {string.Join(',', ModuleType.AllBuiltInModules)}");

            //Return new jspn rpc url
            return new JsonRpcUrl(uri.Scheme, uri.Host, uri.Port, endpoint, enabledModules);
        }

        public string Scheme { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public RpcEndpoint RpcEndpoint { get; set; }
        public string[] EnabledModules { get; set; }

        public override string ToString() => $"{Scheme}://{Host}:{Port}";
        public object Clone() => new JsonRpcUrl(Scheme, Host, Port, RpcEndpoint, EnabledModules);
    }
}

