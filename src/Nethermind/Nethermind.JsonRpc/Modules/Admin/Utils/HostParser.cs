// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class HostParser
    {
        public static string? TryParseHost(string? host)
        {
            if (string.IsNullOrEmpty(host)) 
                return null;
            
            try
            {
                return IPAddress.Parse(host).MapToIPv4().ToString();
            }
            catch (FormatException)
            {
                return host; // Return original if parsing fails - might be hostname
            }
        }
    }
}