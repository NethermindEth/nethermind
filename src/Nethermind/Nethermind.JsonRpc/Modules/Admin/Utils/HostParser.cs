// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class HostParser
    {
        public static string? ParseHost(string? host)
        {
            if (string.IsNullOrEmpty(host)) 
                return null;
            
            try
            {
                var ipAddress = IPAddress.Parse(host);
                
                // Handle IPv4 or IPv4-mapped IPv6
                if (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
                    ipAddress.IsIPv4MappedToIPv6)
                {
                    return ipAddress.MapToIPv4().ToString();
                }
                
                // Return IPv6 as-is
                return ipAddress.ToString();
            }
            catch (Exception)
            {
                return host; // Return original if parsing fails - might be hostname
            }
        }
    }
}