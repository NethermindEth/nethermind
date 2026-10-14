// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Buffers;

namespace Nethermind.Eez.Attester;

internal sealed record StreamingOptions(IPEndPoint ListenAddress, Uri L1Rpc, Uri L2Rpc, ulong L1ChainId, Address Registry, Address Manager,
    string ServerCertificate, string ServerPasswordFile, string[] ComposerCertificates, StreamingRegistryLimits RegistryLimits,
    StreamingSessionLimits SessionLimits, StreamingTransportLimits TransportLimits)
{
    internal static StreamingOptions Load(string path, AttesterOptions attester)
    {
        try
        {
            Settings settings = JsonSerializer.Deserialize<Settings>(ReadFile(path, 64 * 1024), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 16,
            }) ?? throw Invalid();
            if (settings.Profile != "rollup0-calldata" || !IPAddress.IsLoopback(attester.ListenAddress.Address) ||
                settings.L1ChainId == 0 || settings.ComposerCertificates is not { Length: > 0 and <= 16 }) throw Invalid();
            IPEndPoint listen = IPEndPoint.Parse(Required(settings.ListenAddress));
            if (listen.Port != 0 && listen.Port == attester.ListenAddress.Port) throw Invalid();
            Address registry = new(Required(settings.Registry));
            Address manager = new(Required(settings.Manager));
            if (registry == Address.Zero || manager == Address.Zero || settings.MaxSessionBytes > attester.Limits.MaxBytes) throw Invalid();
            StreamingRegistryLimits registryLimits = new(settings.MaxSessions, settings.MaxSessionsPerOwner, settings.MaxReservedBytes,
                TimeSpan.FromSeconds(settings.RetentionSeconds));
            StreamingSessionLimits sessionLimits = new(attester.Limits.MaxBlocks, settings.MaxSessionBytes, attester.Limits.MaxWitnessItems);
            StreamingTransportLimits transportLimits = new(settings.MaxConnections,
                (int)Math.Min(settings.MaxSessionBytes, attester.Limits.MaxMessageBytes), attester.StreamIdleTimeout,
                TimeSpan.FromSeconds(30), attester.RequestTimeout);
            registryLimits.Validate();
            sessionLimits.Validate();
            transportLimits.Validate();
            if (settings.MaxConnections > 64 || settings.MaxSessions > 64 || settings.MaxSessionsPerOwner > settings.MaxSessions ||
                settings.MaxReservedBytes > 8L * 1024 * 1024 * 1024 || settings.MaxSessionBytes > registryLimits.MaxReservedBytes / StreamingSessionRegistry.ReservationCopies)
                throw Invalid();
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string[] pins = new string[settings.ComposerCertificates.Length];
            for (int i = 0; i < pins.Length; i++) pins[i] = Path.GetFullPath(Required(settings.ComposerCertificates[i]), directory);
            return new StreamingOptions(listen, Rpc(settings.L1Rpc), Rpc(settings.L2Rpc), settings.L1ChainId, registry, manager,
                Path.GetFullPath(Required(settings.ServerCertificate), directory), Path.GetFullPath(Required(settings.ServerPasswordFile), directory),
                pins, registryLimits, sessionLimits, transportLimits);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or OverflowException or IOException)
        {
            throw Invalid();
        }
    }

    internal static byte[] ReadFile(string path, int maxBytes)
    {
        using FileStream file = File.OpenRead(path);
        using ArrayPoolDisposableReturn rented = ArrayPoolDisposableReturn.Rent(maxBytes + 1, out byte[] bytes);
        int length = 0;
        while (length <= maxBytes)
        {
            int read = file.Read(bytes, length, maxBytes + 1 - length);
            if (read == 0) break;
            length += read;
        }
        if (length > maxBytes) throw Invalid();
        return bytes.AsSpan(0, length).ToArray();
    }

    private static Uri Rpc(string? value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 ? uri : throw Invalid();

    private static string Required(string? value) => !string.IsNullOrWhiteSpace(value) ? value : throw Invalid();
    internal static FormatException Invalid() => new("invalid streaming configuration");

    private sealed class Settings
    {
        public string? Profile { get; init; }
        public string? ListenAddress { get; init; }
        public string? L1Rpc { get; init; }
        public string? L2Rpc { get; init; }
        public ulong L1ChainId { get; init; }
        public string? Registry { get; init; }
        public string? Manager { get; init; }
        public string? ServerCertificate { get; init; }
        public string? ServerPasswordFile { get; init; }
        public string[]? ComposerCertificates { get; init; }
        public int MaxConnections { get; init; } = 8;
        public int MaxSessions { get; init; } = 8;
        public int MaxSessionsPerOwner { get; init; } = 2;
        public long MaxSessionBytes { get; init; } = 64 * 1024 * 1024;
        public long MaxReservedBytes { get; init; } = 512 * 1024 * 1024;
        public int RetentionSeconds { get; init; } = 600;
    }
}
