// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using NLogLevel = NLog.LogLevel;

namespace Nethermind.Bootnode;

internal static class BootnodeOptionValidation
{
    public static void ValidatePort(string optionName, int value)
    {
        if ((uint)(value - 1) >= ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(optionName, value, $"{optionName} must be between 1 and 65535.");
        }
    }

    public static void ValidatePositive(string optionName, int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(optionName, value, $"{optionName} must be greater than 0.");
        }
    }

    public static void ValidateNonNegative(string optionName, int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(optionName, value, $"{optionName} must be greater than or equal to 0.");
        }
    }

    public static void ValidateLogLevel(string optionName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{optionName} must not be empty.", optionName);
        }

        try
        {
            _ = NLogLevel.FromString(value);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"{optionName} must be one of Trace, Debug, Info, Warn, or Error.", optionName, exception);
        }
    }

    public static void ValidateExternalIp(string optionName, string? value, AddressFamily? expectedFamily)
    {
        if (value is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value) || !TryParseExternalIp(value, out IPAddress? ipAddress))
        {
            throw new ArgumentException($"{optionName} must be a valid IP address.", optionName);
        }

        bool hasExpectedFamily = expectedFamily switch
        {
            AddressFamily.InterNetwork => ipAddress.AddressFamily == AddressFamily.InterNetwork || ipAddress.IsIPv4MappedToIPv6,
            AddressFamily.InterNetworkV6 => ipAddress.AddressFamily == AddressFamily.InterNetworkV6 && !ipAddress.IsIPv4MappedToIPv6,
            null => true,
            _ => false
        };
        if (!hasExpectedFamily || IsUnspecified(ipAddress))
        {
            throw new ArgumentException($"{optionName} must be a usable external IP address.", optionName);
        }
    }

    public static void ValidateHost(string optionName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{optionName} must not be empty.", optionName);
        }

        string host = value.Trim();
        if (host is "*" or "+" ||
            IPAddress.TryParse(host, out _) ||
            Uri.CheckHostName(host) == UriHostNameType.Dns)
        {
            return;
        }

        throw new ArgumentException($"{optionName} must be a valid IP address, DNS name, '*', or '+'.", optionName);
    }

    private static bool IsUnspecified(IPAddress ipAddress)
    {
        if (ipAddress.IsIPv4MappedToIPv6)
        {
            ipAddress = ipAddress.MapToIPv4();
        }

        return ipAddress.Equals(IPAddress.Any) ||
               ipAddress.Equals(IPAddress.IPv6Any) ||
               ipAddress.Equals(IPAddress.None);
    }

    private static bool TryParseExternalIp(string value, [NotNullWhen(true)] out IPAddress? ipAddress)
    {
        if (!IPAddress.TryParse(value, out ipAddress))
        {
            return false;
        }

        return ipAddress.AddressFamily != AddressFamily.InterNetwork ||
               string.Equals(ipAddress.ToString(), value, StringComparison.Ordinal);
    }
}
