// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;

namespace Nethermind.Torrent;

/// <summary>
/// Describes a v1 BitTorrent magnet link without contacting the swarm.
/// </summary>
public sealed class MagnetLink
{
    /// <summary>Maximum accepted magnet URI length.</summary>
    public const int MaxUriLength = 16384;

    private MagnetLink(byte[] infoHash, string? displayName, List<Uri> trackers, List<PeerEndpoint> peers)
    {
        InfoHash = infoHash;
        InfoHashHex = Convert.ToHexString(infoHash).ToLowerInvariant();
        DisplayName = displayName;
        Trackers = trackers.AsReadOnly();
        Peers = peers;
        ExplicitPeers = peers.Select(peer => peer.Host.Contains(':') ? $"[{peer.Host}]:{peer.Port}" : $"{peer.Host}:{peer.Port}").ToArray();
    }

    internal byte[] InfoHash { get; }
    internal IReadOnlyList<PeerEndpoint> Peers { get; }

    /// <summary>Gets the lowercase 40-character v1 infohash.</summary>
    public string InfoHashHex { get; }

    /// <summary>Gets the optional display name, which is not trusted as a file name.</summary>
    public string? DisplayName { get; }

    /// <summary>Gets the distinct HTTP, HTTPS, and UDP announce URLs in link order.</summary>
    public IReadOnlyList<Uri> Trackers { get; }

    /// <summary>Gets the explicit peer hints supplied by the magnet URI.</summary>
    public IReadOnlyList<string> ExplicitPeers { get; }

    /// <summary>Parses a magnet URI containing a v1 btih exact topic.</summary>
    /// <param name="magnetUri">The magnet URI.</param>
    /// <returns>The parsed link.</returns>
    /// <exception cref="FormatException">The URI or its v1 infohash is invalid.</exception>
    public static MagnetLink Parse(string magnetUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(magnetUri);
        if (magnetUri.Length > MaxUriLength || !Uri.TryCreate(magnetUri, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Expected a magnet URI no longer than 16384 characters.");
        }

        byte[]? infoHash = null;
        string? displayName = null;
        List<Uri> trackers = [];
        List<PeerEndpoint> peers = [];
        string query = uri.Query.TrimStart('?');
        foreach (string parameter in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = parameter.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string name = Uri.UnescapeDataString(parameter[..equals]);
            string value = Uri.UnescapeDataString(parameter[(equals + 1)..].Replace('+', ' '));
            if (name.Equals("xt", StringComparison.OrdinalIgnoreCase) && value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
            {
                byte[] candidate = ParseInfoHash(value[9..]);
                if (infoHash is not null && !infoHash.AsSpan().SequenceEqual(candidate))
                {
                    throw new FormatException("Magnet URI contains conflicting btih infohashes.");
                }

                infoHash = candidate;
            }
            else if (name.Equals("dn", StringComparison.OrdinalIgnoreCase))
            {
                displayName ??= value;
            }
            else if (name.Equals("tr", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out Uri? tracker) &&
                    tracker.Scheme is "http" or "https" or "udp" && !trackers.Contains(tracker) && trackers.Count < 64)
                {
                    trackers.Add(tracker);
                }
            }
            else if (name.Equals("x.pe", StringComparison.OrdinalIgnoreCase) && peers.Count < 64 &&
                TryParsePeer(value, out PeerEndpoint peer) && !peers.Contains(peer))
            {
                peers.Add(peer);
            }
        }

        return infoHash is null
            ? throw new FormatException("Magnet URI is missing a v1 urn:btih infohash.")
            : new MagnetLink(infoHash, displayName, trackers, peers);
    }

    private static byte[] ParseInfoHash(string text)
    {
        if (text.Length == 40)
        {
            return Convert.FromHexString(text);
        }

        if (text.Length != 32)
        {
            throw new FormatException("btih infohash must be 40 hex or 32 base32 characters.");
        }

        byte[] hash = new byte[20];
        int bits = 0;
        int accumulator = 0;
        int index = 0;
        foreach (char character in text)
        {
            char upper = char.ToUpperInvariant(character);
            int digit = upper is >= 'A' and <= 'Z' ? upper - 'A' : upper is >= '2' and <= '7' ? upper - '2' + 26 : -1;
            if (digit < 0)
            {
                throw new FormatException("btih infohash contains invalid base32 characters.");
            }

            accumulator = (accumulator << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                hash[index++] = (byte)(accumulator >> bits);
                accumulator &= (1 << bits) - 1;
            }
        }

        return hash;
    }

    internal static bool TryParsePeer(string? value, out PeerEndpoint peer)
    {
        peer = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int colon = value.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
            port is < 1 or > 65535)
        {
            return false;
        }

        string host = value[..colon];
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }
        else if (host.Contains(':'))
        {
            return false;
        }

        if (host.Length is 0 or > 255 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            return false;
        }

        peer = new PeerEndpoint(host, port);
        return true;
    }
}
