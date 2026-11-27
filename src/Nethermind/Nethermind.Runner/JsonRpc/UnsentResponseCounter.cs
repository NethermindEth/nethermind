// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nethermind.Core.Extensions;

namespace Nethermind.Runner.JsonRpc;

internal sealed class UnsentResponseCounter(Startup.TrustedCidr[] trustedProxies)
{
    internal const int CallerShareDivisor = 16;
    internal const int MaxForwardedForLength = 512;
    private const string ForwardedForHeader = "X-Forwarded-For";
    internal static readonly long CountedResponseBytes = 1.MiB;
    internal static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly Dictionary<CallerKey, (long Bytes, int Responses)> _callers = [];
    private long _bytes;
    private long _nextWarningTimestamp;
    private long _suppressedWarnings;

    public static long CallerLimit(long limit) => Math.Max(1, limit / CallerShareDivisor);

    public static bool IsCounted(long responseBytes) => responseBytes > CountedResponseBytes;

    /// <summary>Finds the caller that the limits per address apply to.</summary>
    /// <returns>The caller, or <c>null</c> for a local process, which only the node limit applies to.</returns>
    public CallerKey? ResolveCaller(HttpContext context, out IPAddress? clientAddress, out bool forwarded)
    {
        ConnectionInfo connection = context.Connection;
        IPAddress? peer = connection.RemoteIpAddress;
        clientAddress = peer;
        forwarded = false;
        if (peer is null)
        {
            return new CallerKey(default, connection.Id ?? string.Empty);
        }

        IPAddress normalizedPeer = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        bool loopback = IPAddress.IsLoopback(normalizedPeer);
        if (!loopback && !IsInTrustedNetworks(normalizedPeer))
        {
            return new CallerKey(GetAddressKey(peer), null);
        }

        StringValues forwardedFor = context.Request.Headers[ForwardedForHeader];
        if (forwardedFor.Count == 0)
        {
            return loopback ? null : new CallerKey(GetAddressKey(peer), null);
        }

        IPAddress? client = GetForwardedClient(forwardedFor, loopback);
        if (client is null || IsLoopback(client))
        {
            return new CallerKey(GetAddressKey(peer), null);
        }

        clientAddress = client;
        forwarded = true;
        return new CallerKey(GetAddressKey(client), null);
    }

    private IPAddress? GetForwardedClient(StringValues forwardedFor, bool skipLoopback)
    {
        int remaining = MaxForwardedForLength;
        for (int i = forwardedFor.Count - 1; i >= 0 && remaining > 0; i--)
        {
            ReadOnlySpan<char> value = forwardedFor[i];
            if (value.Length > remaining)
            {
                value = value[^remaining..];
                int firstSeparator = value.IndexOf(',');
                value = firstSeparator < 0 ? [] : value[(firstSeparator + 1)..];
                remaining = 0;
            }
            else
            {
                remaining -= value.Length;
            }

            while (!value.IsEmpty)
            {
                int separator = value.LastIndexOf(',');
                ReadOnlySpan<char> entry = value[(separator + 1)..].Trim();
                value = separator < 0 ? [] : value[..separator];
                if (!TryParseForwardedAddress(entry, out IPAddress? address))
                {
                    return null;
                }

                if (!IsInTrustedNetworks(address) && !(skipLoopback && IsLoopback(address)))
                {
                    return address;
                }
            }
        }

        return null;
    }

    private static bool TryParseForwardedAddress(ReadOnlySpan<char> entry, [NotNullWhen(true)] out IPAddress? address)
    {
        if (IPAddress.TryParse(entry, out address))
        {
            return true;
        }

        if (IPEndPoint.TryParse(entry, out IPEndPoint? endPoint))
        {
            address = endPoint.Address;
            return true;
        }

        return false;
    }

    private bool IsInTrustedNetworks(IPAddress address)
    {
        Startup.TrustedCidr[] networks = trustedProxies;
        for (int i = 0; i < networks.Length; i++)
        {
            if (networks[i].Contains(address)) return true;
        }

        return false;
    }

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    private static UInt128 GetAddressKey(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes[..10].Clear();
            bytes[10] = 0xFF;
            bytes[11] = 0xFF;
            address.TryWriteBytes(bytes[12..], out _);
        }
        else
        {
            address.TryWriteBytes(bytes, out _);
            if (!address.IsIPv4MappedToIPv6)
            {
                bytes[8..].Clear();
            }
        }

        return BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }

    public UnsentResponseLimit TryReserve(CallerKey? caller, long responseBytes, long bytes, bool responseCounted, long limit, int maxResponsesPerCaller, long callerAllowance = 0)
    {
        long callerLimit = CallerLimit(limit) + callerAllowance;
        bool countsNow = !responseCounted && IsCounted(responseBytes + bytes);
        lock (_lock)
        {
            if (_bytes + bytes > limit)
            {
                return UnsentResponseLimit.Node;
            }

            if (caller is not CallerKey key)
            {
                _bytes += bytes;
                return UnsentResponseLimit.None;
            }

            _callers.TryGetValue(key, out (long Bytes, int Responses) state);
            if (state.Bytes > responseBytes && state.Bytes + bytes > callerLimit)
            {
                return UnsentResponseLimit.AddressSize;
            }

            if (countsNow && maxResponsesPerCaller > 0 && state.Responses >= maxResponsesPerCaller)
            {
                return UnsentResponseLimit.AddressResponses;
            }

            _callers[key] = (state.Bytes + bytes, countsNow ? state.Responses + 1 : state.Responses);
            _bytes += bytes;
            return UnsentResponseLimit.None;
        }
    }

    public void Release(CallerKey? caller, long bytes, bool responseCounted)
    {
        lock (_lock)
        {
            if (caller is not CallerKey key)
            {
                _bytes -= Math.Min(bytes, _bytes);
                return;
            }

            if (!_callers.TryGetValue(key, out (long Bytes, int Responses) state))
            {
                return;
            }

            long released = Math.Min(bytes, state.Bytes);
            (long Bytes, int Responses) next = (state.Bytes - released, responseCounted ? Math.Max(0, state.Responses - 1) : state.Responses);
            if (next is { Bytes: 0, Responses: 0 })
            {
                _callers.Remove(key);
            }
            else
            {
                _callers[key] = next;
            }

            _bytes -= released;
        }
    }

    /// <summary>Allows one warning per <see cref="WarningInterval"/>.</summary>
    /// <param name="suppressed">The number of warnings skipped since the last allowed one.</param>
    public bool TryStartWarning(out long suppressed)
    {
        long now = Stopwatch.GetTimestamp();
        long next = Volatile.Read(ref _nextWarningTimestamp);
        if (now < next || Interlocked.CompareExchange(ref _nextWarningTimestamp, now + (long)(WarningInterval.TotalSeconds * Stopwatch.Frequency), next) != next)
        {
            Interlocked.Increment(ref _suppressedWarnings);
            suppressed = 0;
            return false;
        }

        suppressed = Interlocked.Exchange(ref _suppressedWarnings, 0);
        return true;
    }

    internal readonly record struct CallerKey(UInt128 Address, string? ConnectionId);
}

internal enum UnsentResponseLimit
{
    None,
    Node,
    AddressSize,
    AddressResponses
}
