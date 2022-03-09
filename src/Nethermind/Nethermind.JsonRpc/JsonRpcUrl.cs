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
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class JsonRpcUrl : IEquatable<JsonRpcUrl>, ICloneable
    {
        public JsonRpcUrl(string scheme, string host, int port, RpcEndpoint rpcEndpoint, bool isAuthenticated, string[] enabledModules)
        {
            Scheme = scheme;
            Host = host;
            Port = port;
            RpcEndpoint = rpcEndpoint;
            EnabledModules = enabledModules;
            IsAuthenticated = isAuthenticated;
        }

        public static JsonRpcUrl Parse(string packedUrlValue)
        {
            if (packedUrlValue == null)
                throw new ArgumentNullException(nameof(packedUrlValue));

            string[] parts = packedUrlValue.Split('|');
            if (parts.Length != 3 && parts.Length != 4)
                throw new FormatException("Packed url value must contain 3 or 4 parts delimited by '|'");

            string url = parts[0];
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                uri.Segments.Count() > 1 ||
                uri.Port == 0)
                throw new FormatException("First part must be a valid url with the format: scheme://host:port");

            string[] endpointValues = parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (endpointValues.Length == 0)
                throw new FormatException("Second part must contain at least one valid endpoint value delimited by ';'");

            RpcEndpoint endpoint = RpcEndpoint.None;
            foreach (string endpointValue in endpointValues)
            {
                if (Enum.TryParse(endpointValue, ignoreCase: true, out RpcEndpoint parsedEndpoint) &&
                    (parsedEndpoint == RpcEndpoint.Http || parsedEndpoint == RpcEndpoint.Ws))
                    endpoint |= parsedEndpoint;
            }

            if (endpoint == RpcEndpoint.None)
                throw new FormatException($"Second part must contain at least one valid endpoint value (http, https, ws, wss)");

            string[] enabledModules = parts[2].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (enabledModules.Length == 0)
                throw new FormatException("Third part must contain at least one module delimited by ';'");

            bool isAuthenticated = enabledModules.Any(m => m.ToLower() == "engine");

            // Check if authentication disabled for this url
            if (parts.Length == 4)
            {
                if (parts[3] != "no-auth")
                {
                    throw new FormatException("Fourth part should be \"no-auth\"");
                }

                isAuthenticated = false;
            }

            JsonRpcUrl result = new (uri.Scheme, uri.Host, uri.Port, endpoint, isAuthenticated, enabledModules);

           return result;
        }

        public bool IsAuthenticated { get; private set; }
        public string Scheme { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public RpcEndpoint RpcEndpoint { get; set; }
        public IReadOnlyCollection<string> EnabledModules { get; set; }

        public bool Equals(JsonRpcUrl other)
        {
            if (other == null)
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return string.Equals(Scheme, other.Scheme) &&
                   string.Equals(Host, other.Host) &&
                   Port == other.Port &&
                   RpcEndpoint == other.RpcEndpoint &&
                   EnabledModules.SequenceEqual(other.EnabledModules);
        }

        public override bool Equals(object other)
        {
            if (other == null)
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return other is JsonRpcUrl url && Equals(url);
        }

        public override int GetHashCode() => HashCode.Combine(Scheme, Host, Port, RpcEndpoint, EnabledModules as IStructuralEquatable);
        public object Clone() => new JsonRpcUrl(Scheme, Host, Port, RpcEndpoint, IsAuthenticated, EnabledModules as string[]);
        public override string ToString() => $"{Scheme}://{Host}:{Port}";
    }
}

