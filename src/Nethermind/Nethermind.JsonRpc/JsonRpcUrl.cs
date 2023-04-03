// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FastEnumUtility;
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
            EnabledModules = new HashSet<string>(enabledModules, StringComparer.InvariantCultureIgnoreCase);
            IsAuthenticated = isAuthenticated;
        }

        public static JsonRpcUrl Parse(string packedUrlValue)
        {
            if (packedUrlValue is null)
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
                if (FastEnum.TryParse(endpointValue, ignoreCase: true, out RpcEndpoint parsedEndpoint) &&
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

            JsonRpcUrl result = new(uri.Scheme, uri.Host, uri.Port, endpoint, isAuthenticated, enabledModules);

            return result;
        }

        public bool IsAuthenticated { get; }
        public string Scheme { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public RpcEndpoint RpcEndpoint { get; set; }
        public IReadOnlyCollection<string> EnabledModules { get; set; }

        public bool IsModuleEnabled(string moduleName) =>
            EnabledModules.Any(m => StringComparer.InvariantCultureIgnoreCase.Equals(m, moduleName));

        public bool Equals(JsonRpcUrl other)
        {
            if (other is null)
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return string.Equals(Scheme, other.Scheme) &&
                   string.Equals(Host, other.Host) &&
                   Port == other.Port &&
                   RpcEndpoint == other.RpcEndpoint &&
                   IsAuthenticated == other.IsAuthenticated &&
                   EnabledModules.OrderBy(t => t).SequenceEqual(other.EnabledModules.OrderBy(t => t),
                       StringComparer.InvariantCultureIgnoreCase);
        }

        public override bool Equals(object other)
        {
            if (other is null)
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return other is JsonRpcUrl url && Equals(url);
        }

        public override int GetHashCode() => HashCode.Combine(Scheme, Host, Port, RpcEndpoint, EnabledModules as IStructuralEquatable);
        public object Clone() => new JsonRpcUrl(Scheme, Host, Port, RpcEndpoint, IsAuthenticated, EnabledModules.ToArray());
        public override string ToString() => $"{Scheme}://{Host}:{Port}";
    }
}

