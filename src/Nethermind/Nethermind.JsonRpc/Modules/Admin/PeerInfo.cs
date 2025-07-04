// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using Nethermind.Network;
using Nethermind.Stats.Model;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class PeerInfo
    {
        public string Enr { get; set; }
        public string Enode { get; set; }
        public string Id { get; }
        public string Name { get; set; }
        public string[] Caps { get; set; }
        public NetworkInfo Network { get; set; }
        public Dictionary<string, object> Protocols { get; set; }

        // Legacy fields for backward compatibility
        public string Host { get; set; }
        public int Port { get; set; }
        public string Address { get; set; }
        public bool IsBootnode { get; set; }
        public bool IsTrusted { get; set; }
        public bool IsStatic { get; set; }
        public string ClientType { get; set; }
        public string EthDetails { get; set; }
        public string LastSignal { get; set; }
        public bool Inbound { get; set; }

        public PeerInfo()
        {
        }

        public PeerInfo(Peer peer, bool includeDetails)
        {
            if (peer.Node is null)
            {
                throw new ArgumentException(
                    $"{nameof(PeerInfo)} cannot be created for a {nameof(Peer)} with an unknown {peer.Node}");
            }

            // Standard format fields
            Id = peer.Node.Id.Hash.ToString(false);
            Name = peer.Node.ClientId;
            Enode = peer.Node.ToString(Node.Format.ENode);
            
            // ENR - currently not directly available, would need implementation
            Enr = null; // TODO: Implement ENR retrieval when available
            
            // Capabilities - extract from session if available
            var capabilities = new List<string>();
            var session = peer.InSession ?? peer.OutSession;
            if (session != null)
            {
                // Try to get capabilities from session
                try
                {
                    // Use reflection to get capabilities if available
                    var sessionType = session.GetType();
                    var supportedCapabilitiesProperty = sessionType.GetProperty("SupportedCapabilities");
                    var agreedCapabilitiesProperty = sessionType.GetProperty("AgreedCapabilities");
                    
                    if (agreedCapabilitiesProperty != null && agreedCapabilitiesProperty.GetValue(session) is IEnumerable<object> agreedCaps)
                    {
                        foreach (var cap in agreedCaps)
                        {
                            var capType = cap.GetType();
                            var protocolCode = capType.GetProperty("ProtocolCode")?.GetValue(cap)?.ToString();
                            var version = capType.GetProperty("Version")?.GetValue(cap);
                            
                            if (protocolCode != null && version != null)
                            {
                                capabilities.Add($"{protocolCode}/{version}");
                            }
                        }
                    }
                    else if (supportedCapabilitiesProperty != null && supportedCapabilitiesProperty.GetValue(session) is IEnumerable<object> supportedCaps)
                    {
                        foreach (var cap in supportedCaps)
                        {
                            var capType = cap.GetType();
                            var protocolCode = capType.GetProperty("ProtocolCode")?.GetValue(cap)?.ToString();
                            var version = capType.GetProperty("Version")?.GetValue(cap);
                            
                            if (protocolCode != null && version != null)
                            {
                                capabilities.Add($"{protocolCode}/{version}");
                            }
                        }
                    }
                }
                catch
                {
                    // Fallback to parsing from EthDetails if reflection fails
                }
                
                // Fallback to EthDetails if we couldn't get capabilities from session
                if (capabilities.Count == 0 && !string.IsNullOrEmpty(peer.Node.EthDetails))
                {
                    capabilities.Add(peer.Node.EthDetails);
                }
            }
            
            Caps = capabilities.ToArray();
            
            // Network information
            var isInbound = peer.InSession is not null;
            var localAddress = "";
            var remoteAddress = peer.Node.Address.ToString();
            
            // Try to get more detailed network information from session
            if (session != null)
            {
                try
                {
                    var sessionType = session.GetType();
                    var localPortProperty = sessionType.GetProperty("LocalPort");
                    var remoteHostProperty = sessionType.GetProperty("RemoteHost");
                    var remotePortProperty = sessionType.GetProperty("RemotePort");
                    
                    if (localPortProperty != null && remoteHostProperty != null && remotePortProperty != null)
                    {
                        var localPort = localPortProperty.GetValue(session);
                        var remoteHost = remoteHostProperty.GetValue(session);
                        var remotePort = remotePortProperty.GetValue(session);
                        
                        // Build local address - we may not have the local IP, so use placeholder
                        localAddress = $"127.0.0.1:{localPort}";
                        remoteAddress = $"{remoteHost}:{remotePort}";
                    }
                }
                catch
                {
                    // Fallback to node address if session inspection fails
                }
            }
            
            Network = new NetworkInfo
            {
                LocalAddress = localAddress,
                RemoteAddress = remoteAddress,
                Inbound = isInbound,
                Trusted = peer.Node.IsTrusted,
                Static = peer.Node.IsStatic
            };
            
            // Protocols - construct protocol information
            Protocols = new Dictionary<string, object>();
            
            // Add ETH protocol info if available
            if (!string.IsNullOrEmpty(peer.Node.EthDetails))
            {
                var ethProtocol = new Dictionary<string, object>();
                
                // Try to parse version from EthDetails (e.g., "eth68" -> 68)
                if (peer.Node.EthDetails.StartsWith("eth"))
                {
                    var versionStr = peer.Node.EthDetails.Substring(3);
                    if (int.TryParse(versionStr, out var version))
                    {
                        ethProtocol["version"] = version;
                    }
                }
                
                // Add other ETH protocol fields - these would need to be populated from sync peer data
                ethProtocol["earliestBlock"] = 0;
                ethProtocol["latestBlock"] = 0;
                ethProtocol["latestBlockHash"] = "0x0000000000000000000000000000000000000000000000000000000000000000";
                
                Protocols["eth"] = ethProtocol;
            }
            
            // Legacy fields for backward compatibility
            Host = peer.Node.Host is null ? null : IPAddress.Parse(peer.Node.Host).MapToIPv4().ToString();
            Port = peer.Node.Port;
            Address = peer.Node.Address.ToString();
            IsBootnode = peer.Node.IsBootnode;
            IsStatic = peer.Node.IsStatic;
            IsTrusted = peer.Node.IsTrusted;
            Inbound = isInbound;

            if (includeDetails)
            {
                ClientType = peer.Node.ClientType.ToString();
                EthDetails = peer.Node.EthDetails;
                LastSignal = (peer.InSession ?? peer.OutSession!).LastPingUtc.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
    
    public class NetworkInfo
    {
        public string LocalAddress { get; set; }
        public string RemoteAddress { get; set; }
        public bool Inbound { get; set; }
        public bool Trusted { get; set; }
        public bool Static { get; set; }
    }
}
