// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using System.Net;

namespace Nethermind.Network.Enr;

/// <summary>
/// Represents an Ethereum Node Record (ENR) as defined in https://eips.ethereum.org/EIPS/eip-778
/// </summary>
public class NodeRecord
{
    private static readonly IEcdsa DefaultEcdsa = new Ecdsa();

    private ulong _enrSequence;

    private string? _enrString;

    private Hash256? _contentHash;

    private Signature? _signature;

    private SortedDictionary<string, EnrContentEntry> Entries { get; } = new(StringComparer.Ordinal);

    internal byte[]? OriginalRlp { get; set; }

    internal byte[]? OriginalContentRlp { get; set; }

    /// <summary>
    /// Represents the version / id / sequence of the node record data. It should be increased by one with each
    /// update to the node data. Setting sequence on this class wipes out <see cref="EnrString"/> and
    /// <see cref="ContentHash"/>.
    /// </summary>
    public ulong EnrSequence
    {
        get => _enrSequence;
        set
        {
            if (_enrSequence != value)
            {
                _enrSequence = value;
                _enrString = null;
                _contentHash = null;
                Signature = null;
            }
        }
    }

    /// <summary>
    /// A base64 string representing a node record with the 'enr:' prefix
    /// enr:-IS4QHCYrYZbAK(...)WM0xOIN1ZHCCdl8
    /// </summary>
    public string EnrString
    {
        get
        {
            return _enrString ??= CreateEnrString();
        }
    }

    /// <summary>
    /// Hash of the content, i.e. Keccak([seq, k, v, ...]) as defined in https://eips.ethereum.org/EIPS/eip-778
    /// </summary>
    public Hash256 ContentHash
    {
        get
        {
            return _contentHash ??= CalculateContentHash();
        }
    }

    private Hash256 CalculateContentHash()
    {
        if (OriginalContentRlp is not null)
        {
            return ValueKeccak.Compute(OriginalContentRlp).ToCommitment();
        }

        KeccakRlpStream rlpStream = new();
        EncodeContent(rlpStream);
        return rlpStream.GetHash();
    }

    /// <summary>
    /// A signature resulting from a secp256k1 signing of the [seq, k, v, ...] content.
    /// </summary>
    public Signature? Signature
    {
        get => _signature;
        set
        {
            _signature = value;
            OriginalRlp = null;
            OriginalContentRlp = null;
            _enrString = null;
            _contentHash = null;
        }
    }

    public bool Snap { get; set; }

    public NodeRecord() => SetEntry(IdEntry.Instance);

    /// <summary>
    /// Gets the IP address advertised for discovery traffic.
    /// </summary>
    /// <remarks>
    /// IPv4 is preferred when both <c>ip</c> and <c>udp</c> are present. Otherwise IPv6 is returned when it has a
    /// discovery port, with <c>udp</c> as the EIP-778 fallback.
    /// </remarks>
    public IPAddress? DiscoveryIp => GetDiscoveryEndpoint().Ip;

    /// <summary>
    /// Gets the UDP port advertised for discovery traffic.
    /// </summary>
    /// <remarks>
    /// For IPv6, <c>udp6</c> is preferred and <c>udp</c> is used as the EIP-778 fallback.
    /// </remarks>
    public int? DiscoveryPort => GetDiscoveryEndpoint().Port;

    /// <summary>
    /// Gets the IP address advertised for RLPx TCP traffic.
    /// </summary>
    /// <remarks>
    /// IPv4 is preferred when both <c>ip</c> and <c>tcp</c> are present. Otherwise IPv6 is returned when it has a
    /// TCP port, with <c>tcp</c> as the EIP-778 fallback.
    /// </remarks>
    public IPAddress? TcpIp => GetTcpEndpoint().Ip;

    /// <summary>
    /// Gets the TCP port advertised for RLPx traffic.
    /// </summary>
    /// <remarks>
    /// For IPv6, <c>tcp6</c> is preferred and <c>tcp</c> is used as the EIP-778 fallback.
    /// </remarks>
    public int? TcpPort => GetTcpEndpoint().Port;

    private (IPAddress? Ip, int? Port) GetDiscoveryEndpoint()
    {
        IPAddress? ip = GetObj<IPAddress>(EnrContentKey.Ip);
        int? udp = GetValue<int>(EnrContentKey.Udp);
        if (ip is not null && udp is not null)
        {
            return (ip, udp);
        }

        IPAddress? ip6 = GetObj<IPAddress>(EnrContentKey.Ip6);
        int? udp6 = GetValue<int>(EnrContentKey.Udp6);
        if (ip6 is not null)
        {
            int? port = udp6 ?? udp;
            return port is null ? (null, null) : (ip6, port);
        }

        return (null, null);
    }

    private (IPAddress? Ip, int? Port) GetTcpEndpoint()
    {
        IPAddress? ip = GetObj<IPAddress>(EnrContentKey.Ip);
        int? tcp = GetValue<int>(EnrContentKey.Tcp);
        if (ip is not null && tcp is not null)
        {
            return (ip, tcp);
        }

        IPAddress? ip6 = GetObj<IPAddress>(EnrContentKey.Ip6);
        int? tcp6 = GetValue<int>(EnrContentKey.Tcp6);
        if (ip6 is not null)
        {
            int? port = tcp6 ?? tcp;
            return port is null ? (null, null) : (ip6, port);
        }

        return (null, null);
    }

    public static NodeRecord FromEnrString(string enrString)
    {
        const string prefix = "enr:";
        if (!enrString.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("ENR must start with the 'enr:' prefix.", nameof(enrString));
        }

        string base64 = enrString[prefix.Length..].Replace('-', '+').Replace('_', '/');
        int padding = (4 - base64.Length % 4) % 4;
        if (padding is not 0)
        {
            base64 = string.Concat(base64, new string('=', padding));
        }

        NodeRecord nodeRecord = FromBytes(Convert.FromBase64String(base64));
        nodeRecord._enrString = enrString;
        return nodeRecord;
    }

    public static NodeRecord FromBytes(ReadOnlySpan<byte> bytes)
        => FromBytes(bytes, DefaultEcdsa);

    public static NodeRecord FromBytes(byte[] bytes)
        => FromBytes(bytes.AsSpan(), DefaultEcdsa);

    public static NodeRecord FromBytes(byte[] bytes, IEcdsa ecdsa)
        => FromBytes(bytes.AsSpan(), ecdsa);

    public static NodeRecord FromBytes(ReadOnlySpan<byte> bytes, IEcdsa ecdsa)
    {
        ArgumentNullException.ThrowIfNull(ecdsa);

        NodeRecordSigner signer = new(ecdsa);
        Rlp.ValueDecoderContext ctx = new(bytes);
        NodeRecord nodeRecord = signer.Deserialize(ref ctx);
        if (ctx.Position != bytes.Length)
        {
            throw new RlpException("Unexpected trailing bytes in ENR.");
        }

        if (!signer.Verify(nodeRecord))
        {
            throw new RlpException("Invalid ENR signature.");
        }

        return nodeRecord;
    }

    /// <summary>
    /// Sets one of the record entries. Entries are then automatically sorted by keys.
    /// </summary>
    /// <param name="entry"></param>
    public void SetEntry(EnrContentEntry entry)
    {
        if (Entries.ContainsKey(entry.Key))
        {
            EnrSequence++;
        }

        Entries[entry.Key] = entry;
        OriginalRlp = null;
        OriginalContentRlp = null;
        _enrString = null;
        _contentHash = null;
        _signature = null;
    }

    /// <summary>
    /// Checks whether an ENR entry with the specified key is present.
    /// </summary>
    /// <param name="entryKey">Key of the entry to check.</param>
    /// <returns><see langword="true"/> when the entry is present; otherwise <see langword="false"/>.</returns>
    public bool HasEntry(string entryKey) => Entries.ContainsKey(entryKey);

    /// <summary>
    /// Gets a record entry value (in case of the value types). Use <see cref="GetObj{TValue}"/> for reference types./>
    /// </summary>
    /// <param name="entryKey">Key of the entry to retrieve.</param>
    /// <typeparam name="TValue">Type of the entry value.</typeparam>
    /// <returns>Value of the entry or <value>null</value> if the entry is missing.</returns>
    public TValue? GetValue<TValue>(string entryKey) where TValue : struct
    {
        if (Entries.TryGetValue(entryKey, out EnrContentEntry? value))
        {
            EnrContentEntry<TValue> entry = (EnrContentEntry<TValue>)value;
            return entry.Value;
        }

        return null;
    }

    /// <summary>
    /// Gets a record entry value (in case of the ref types). Use <see cref="GetValue{TValue}"/> for value types./>
    /// </summary>
    /// <param name="entryKey">Key of the entry to retrieve.</param>
    /// <typeparam name="TValue">Type of the entry value.</typeparam>
    /// <returns>Value of the entry or <value>null</value> if the entry is missing.</returns>
    public TValue? GetObj<TValue>(string entryKey) where TValue : class
    {
        if (Entries.TryGetValue(entryKey, out EnrContentEntry? value))
        {
            EnrContentEntry<TValue> entry = (EnrContentEntry<TValue>)value;
            return entry.Value;
        }

        return null;
    }

    /// <summary>
    /// Needed for an optimized content serialization. We serialize content to calculate signatures.
    /// </summary>
    /// <returns>Length of the Rlp([seq, k, v, ...]) when calculated without the RLP sequence prefix.</returns>
    private int GetContentLengthWithoutSignature()
    {
        int contentLength =
            Rlp.LengthOf(EnrSequence); // this is a different meaning of a sequence than the RLP sequence
        foreach ((_, EnrContentEntry enrContentEntry) in Entries)
        {
            contentLength += enrContentEntry.GetRlpLength();
        }

        return contentLength;
    }

    /// <summary>
    /// Needed for optimized RLP serialization.
    /// </summary>
    /// <returns>Length of the Rlp([signature, seq, k, v, ...]) when calculated without the RLP sequence prefix.</returns>
    private int GetContentLengthWithSignature() => GetContentLengthWithoutSignature() + 64 + 2;

    /// <summary>
    /// Needed for optimized RLP serialization when a proper length byte array has to be allocated upfront.
    /// </summary>
    /// <returns>Length of the Rlp([signature, seq, k, v, ...])</returns>
    public int GetRlpLengthWithSignature() => OriginalRlp?.Length ?? Rlp.LengthOfSequence(
        GetContentLengthWithSignature());

    /// <summary>
    /// Applies Rlp([seq, k, v, ...]]).
    /// </summary>
    /// <param name="rlpStream">An RLP stream to encode the content to.</param>
    private void EncodeContent(RlpStream rlpStream)
    {
        int contentLength = GetContentLengthWithoutSignature();
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(EnrSequence);
        foreach ((_, EnrContentEntry contentEntry) in Entries)
        {
            contentEntry.Encode(rlpStream);
        }
    }

    /// <summary>
    /// Added here for diagnostic purposes - hes is easier to read and compare.
    /// </summary>
    /// <returns>Rlp([signature, seq, k, v, ...]) as a hex string</returns>
    public string GetHex() => ToRlpBytes().AsSpan().ToHexString();

    public byte[] ToRlpBytes()
    {
        if (OriginalRlp is not null)
        {
            return OriginalRlp.ToArray();
        }

        int rlpLength = GetRlpLengthWithSignature();
        byte[] bytes = GC.AllocateUninitializedArray<byte>(rlpLength);
        RlpStream rlpStream = new(bytes);
        Encode(rlpStream);
        return bytes;
    }

    /// <summary>
    /// Applies Rlp([signature, seq, k, v, ...]]).
    /// </summary>
    /// <param name="rlpStream">An RLP stream to encode the content to.</param>
    public void Encode(RlpStream rlpStream)
    {
        if (OriginalRlp is not null)
        {
            rlpStream.Write(OriginalRlp);
            return;
        }

        RequireSignature();

        int contentLength = GetContentLengthWithSignature();
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(Signature!.Bytes);
        rlpStream.Encode(EnrSequence); // a different sequence here (not RLP sequence)
        foreach ((_, EnrContentEntry contentEntry) in Entries)
        {
            contentEntry.Encode(rlpStream);
        }
    }

    private string CreateEnrString()
    {
        RequireSignature();

        const string prefix = "enr:";
        string base64String = Convert.ToBase64String(ToRlpBytes()).Replace('+', '-').Replace('/', '_');
        int skipLast = base64String[^2] == '=' ? 2 : base64String[^1] == '=' ? 1 : 0;
        return string.Concat(prefix, base64String.AsSpan(0, base64String.Length - skipLast));
    }

    private void RequireSignature()
    {
        if (Signature is null)
        {
            throw new Exception("Cannot encode a node record with an empty signature.");
        }
    }
}
