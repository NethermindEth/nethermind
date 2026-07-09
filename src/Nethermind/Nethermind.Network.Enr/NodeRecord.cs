// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

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

    /// <summary>
    /// Represents the version / id / sequence of the node record data. It should be increased by one with each
    /// update to the node data. Setting sequence on this class wipes out <see cref="ToString"/> and
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
    /// Returns a base64 string representing a signed node record with the <c>enr:</c> prefix.
    /// </summary>
    public override string ToString() => _enrString ??= CreateEnrString();

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
        KeccakRlpWriter writer = new();
        EncodeContent(ref writer);
        return writer.GetHash();
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
            _enrString = null;
            _contentHash = null;
        }
    }

    public bool Snap { get; set; }

    public NodeRecord() => SetEntry(IdEntry.Instance);

    /// <summary>
    /// Gets the IP address advertised for node traffic.
    /// </summary>
    public IPAddress? Ip =>
        TryGetTcpEndpoint(out IPEndPoint? tcpEndpoint) ? tcpEndpoint.Address :
        TryGetDiscoveryEndpoint(out IPEndPoint? discoveryEndpoint) ? discoveryEndpoint.Address :
        GetObj<IPAddress>(EnrContentKey.Ip) ?? GetObj<IPAddress>(EnrContentKey.Ip6);

    /// <summary>
    /// Gets the UDP port advertised for discovery traffic.
    /// </summary>
    /// <remarks>
    /// For IPv6, <c>udp6</c> is preferred and <c>udp</c> is used as the EIP-778 fallback.
    /// </remarks>
    public int? DiscoveryPort => TryGetDiscoveryEndpoint(out IPEndPoint? endpoint) ? endpoint.Port : null;

    /// <summary>
    /// Gets the TCP port advertised for RLPx traffic.
    /// </summary>
    /// <remarks>
    /// For IPv6, <c>tcp6</c> is preferred and <c>tcp</c> is used as the EIP-778 fallback.
    /// </remarks>
    public int? TcpPort => TryGetTcpEndpoint(out IPEndPoint? endpoint) ? endpoint.Port : null;

    /// <summary>
    /// Tries to get the UDP discovery endpoint from matching ENR address and port entries.
    /// </summary>
    /// <param name="endpoint">The discovery endpoint when the ENR contains a usable UDP endpoint.</param>
    /// <returns><see langword="true"/> when a usable discovery endpoint is present; otherwise <see langword="false"/>.</returns>
    public bool TryGetDiscoveryEndpoint([MaybeNullWhen(false)] out IPEndPoint endpoint)
        => TryGetEndpoint(EnrContentKey.Udp, EnrContentKey.Udp6, out endpoint);

    /// <summary>
    /// Tries to get the TCP RLPx endpoint from matching ENR address and port entries.
    /// </summary>
    /// <param name="endpoint">The TCP endpoint when the ENR contains a usable RLPx endpoint.</param>
    /// <returns><see langword="true"/> when a usable TCP endpoint is present; otherwise <see langword="false"/>.</returns>
    public bool TryGetTcpEndpoint([MaybeNullWhen(false)] out IPEndPoint endpoint)
        => TryGetEndpoint(EnrContentKey.Tcp, EnrContentKey.Tcp6, out endpoint);

    private bool TryGetEndpoint(string ipv4PortKey, string ipv6PortKey, [MaybeNullWhen(false)] out IPEndPoint endpoint)
    {
        IPAddress? ip = GetObj<IPAddress>(EnrContentKey.Ip);
        if (ip is not null && TryGetPort(ipv4PortKey, out int port))
        {
            endpoint = new IPEndPoint(ip, port);
            return true;
        }

        IPAddress? ip6 = GetObj<IPAddress>(EnrContentKey.Ip6);
        if (ip6 is not null && (TryGetPort(ipv6PortKey, out port) || TryGetPort(ipv4PortKey, out port)))
        {
            endpoint = new IPEndPoint(ip6, port);
            return true;
        }

        endpoint = null;
        return false;
    }

    private bool TryGetPort(string portKey, out int port)
    {
        int? value = GetValue<int>(portKey);
        if (value is null || value.Value == 0 || (uint)value.Value > ushort.MaxValue)
        {
            port = 0;
            return false;
        }

        port = value.Value;
        return true;
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
        RlpReader reader = new(bytes);
        NodeRecord nodeRecord = signer.Deserialize(ref reader);
        if (reader.Position != bytes.Length)
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
    /// <param name="writer">An RLP writer to encode the content to.</param>
    private void EncodeContent<TWriter>(ref TWriter writer)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int contentLength = GetContentLengthWithoutSignature();
        writer.StartSequence(contentLength);
        writer.Encode(EnrSequence);
        foreach ((_, EnrContentEntry contentEntry) in Entries)
        {
            contentEntry.Encode(ref writer);
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
        RlpWriter writer = new(bytes);
        Encode(ref writer);
        return bytes;
    }

    /// <summary>
    /// Applies Rlp([signature, seq, k, v, ...]]).
    /// </summary>
    /// <param name="writer">An RLP writer to encode the content to.</param>
    public void Encode<TWriter>(ref TWriter writer)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (OriginalRlp is not null)
        {
            writer.Write(OriginalRlp);
            return;
        }

        RequireSignature();

        int contentLength = GetContentLengthWithSignature();
        writer.StartSequence(contentLength);
        writer.Encode(Signature!.Bytes);
        writer.Encode(EnrSequence);
        foreach ((_, EnrContentEntry contentEntry) in Entries)
        {
            contentEntry.Encode(ref writer);
        }
    }

    private string CreateEnrString()
    {
        RequireSignature();

        const string prefix = "enr:";
        if (OriginalRlp is not null)
        {
            return string.Concat(prefix, Base64Url.EncodeToString(OriginalRlp));
        }

        using ArrayPoolSpan<byte> bytes = new(GetRlpLengthWithSignature());
        RlpWriter writer = new(bytes);
        Encode(ref writer);
        return string.Concat(prefix, Base64Url.EncodeToString(bytes));
    }

    private void RequireSignature()
    {
        if (Signature is null)
        {
            throw new Exception("Cannot encode a node record with an empty signature.");
        }
    }

}
