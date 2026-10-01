// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Torrent;

/// <summary>A BEP 46 magnet feed identified by an Ed25519 public key and optional salt.</summary>
public sealed class Bep46Link
{
    private Bep46Link(byte[] publicKey, byte[] salt, string originalUri)
    {
        PublicKey = publicKey;
        Salt = salt;
        OriginalUri = originalUri;
    }

    /// <summary>Gets the 32-byte publisher public key.</summary>
    public byte[] PublicKey { get; }

    /// <summary>Gets the optional binary salt.</summary>
    public byte[] Salt { get; }

    /// <summary>Gets the original URI, including tracker and peer hints.</summary>
    public string OriginalUri { get; }

    /// <summary>Signs a feed update containing a 20-byte v1 torrent infohash.</summary>
    public DhtMutableItem SignUpdate(ReadOnlySpan<byte> privateSeed, ReadOnlySpan<byte> infoHash, long sequence)
    {
        if (infoHash.Length != 20)
        {
            throw new ArgumentException("BEP 46 infohash must be 20 bytes.", nameof(infoHash));
        }

        byte[] value = Bencode.Encode(Bencode.Dictionary(new KeyValuePair<string, BValue>("ih", Bencode.Bytes(infoHash))));
        DhtMutableItem item = DhtMutableItem.Sign(privateSeed, value, sequence, Salt);
        if (!item.PublicKey.AsSpan().SequenceEqual(PublicKey))
        {
            throw new ArgumentException("Private seed does not match the feed public key.", nameof(privateSeed));
        }

        return item;
    }

    /// <summary>Parses a BEP 46 magnet link with an xs=urn:btpk: key.</summary>
    public static Bep46Link Parse(string uriText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriText);
        if (uriText.Length > MagnetLink.MaxUriLength || !Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Expected a valid BEP 46 magnet URI.");
        }

        byte[]? publicKey = null;
        byte[] salt = [];
        bool hasSalt = false;
        foreach (string parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = parameter.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string name = Uri.UnescapeDataString(parameter[..equals]);
            string value = Uri.UnescapeDataString(parameter[(equals + 1)..]);
            if (name.Equals("xs", StringComparison.OrdinalIgnoreCase) && value.StartsWith("urn:btpk:", StringComparison.OrdinalIgnoreCase))
            {
                byte[] candidate = ParseHex(value[9..], 32, "btpk key");
                if (publicKey is not null && !publicKey.AsSpan().SequenceEqual(candidate))
                {
                    throw new FormatException("BEP 46 magnet contains conflicting publisher keys.");
                }

                publicKey = candidate;
            }
            else if (name.Equals("s", StringComparison.OrdinalIgnoreCase))
            {
                byte[] candidate = ParseHex(value, 64, "salt");
                if (hasSalt && !salt.AsSpan().SequenceEqual(candidate))
                {
                    throw new FormatException("BEP 46 magnet contains conflicting salts.");
                }

                salt = candidate;
                hasSalt = true;
            }
        }

        return publicKey is null
            ? throw new FormatException("BEP 46 magnet is missing xs=urn:btpk:.")
            : new Bep46Link(publicKey, salt, uriText);
    }

    /// <summary>Attempts to identify and parse a BEP 46 magnet link.</summary>
    public static bool TryParse(string uriText, out Bep46Link? link)
    {
        link = null;
        if (uriText is null)
        {
            return false;
        }

        try
        {
            link = Parse(uriText);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Fetches the latest signed pointer currently visible in the DHT.</summary>
    public async Task<Bep46Update?> GetCurrentAsync(CancellationToken token)
    {
        await using DhtItemClient client = new();
        return await GetCurrentAsync(client.GetMutableAsync, token);
    }

    internal async Task<Bep46Update?> GetCurrentAsync(
        Func<byte[], byte[], CancellationToken, Task<DhtMutableItem?>> getMutable, CancellationToken token)
    {
        DhtMutableItem? item = await getMutable(PublicKey, Salt, token);
        return item is null ? null : Decode(item);
    }

    /// <summary>Resolves this feed to a regular v1 magnet URI, retaining source hints.</summary>
    public async Task<string> ResolveMagnetAsync(CancellationToken token)
    {
        Bep46Update update = await GetCurrentAsync(token)
            ?? throw new InvalidOperationException("No valid BEP 46 update was found in the DHT.");
        return ToMagnet(update);
    }

    /// <summary>Converts a verified update into a v1 magnet URI while retaining tracker and peer hints.</summary>
    public string ToMagnet(Bep46Update update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.InfoHash.Length != 20)
        {
            throw new ArgumentException("BEP 46 infohash must be 20 bytes.", nameof(update));
        }

        Uri uri = new(OriginalUri);
        List<string> parameters = [];
        foreach (string parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = parameter.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string name = Uri.UnescapeDataString(parameter[..equals]);
            if (name.Equals("xs", StringComparison.OrdinalIgnoreCase) || name.Equals("s", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("xt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            parameters.Add(parameter);
        }

        parameters.Insert(0, $"xt=urn:btih:{Convert.ToHexString(update.InfoHash)}");
        return "magnet:?" + string.Join('&', parameters);
    }

    internal static Bep46Update Decode(DhtMutableItem item)
    {
        BDictionary value = BencodeDocument.Decode(item.Value, requireCanonical: true).Root.AsDictionary("BEP 46 value");
        if (!value.TryGetValue("ih", out BValue? rawHash) || rawHash is not BString hash || hash.Bytes.Length != 20)
        {
            throw new FormatException("BEP 46 value must contain a 20-byte ih.");
        }

        return new Bep46Update(item.Sequence, hash.Bytes.ToArray());
    }

    private static byte[] ParseHex(string text, int maxBytes, string label)
    {
        if (text.Length > maxBytes * 2 || (text.Length & 1) != 0)
        {
            throw new FormatException($"Invalid BEP 46 {label}.");
        }

        byte[] result = Convert.FromHexString(text);
        if (label == "btpk key" && result.Length != 32)
        {
            throw new FormatException("BEP 46 publisher key must be 32 bytes.");
        }

        return result;
    }
}

/// <summary>A verified BEP 46 feed update.</summary>
public sealed record Bep46Update(long Sequence, byte[] InfoHash);
