// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-778
/// </summary>
public class NodeRecordSigner(IEcdsa? ethereumEcdsa, PrivateKey? privateKey = null) : INodeRecordSigner
{
    private readonly IEcdsa _ecdsa = ethereumEcdsa ?? throw new ArgumentNullException(nameof(ethereumEcdsa));

    private readonly PrivateKey? _privateKey = privateKey;

    /// <summary>
    /// Signs the node record with own private key.
    /// </summary>
    /// <param name="nodeRecord"></param>
    public void Sign(NodeRecord nodeRecord)
    {
        if (_privateKey is null)
        {
            throw new InvalidOperationException("Cannot sign an ENR without a private key.");
        }

        nodeRecord.OriginalRlp = null;
        nodeRecord.Signature = _ecdsa.Sign(_privateKey, in nodeRecord.ContentHash.ValueHash256);
    }

    /// <summary>
    /// Deserializes a <see cref="NodeRecord"/> from a <see cref="RlpReader"/>.
    /// </summary>
    /// <param name="reader">The RLP reader to read the serialized data from.</param>
    /// <returns>A deserialized <see cref="NodeRecord"/></returns>
    public NodeRecord Deserialize(ref RlpReader reader)
        => DeserializeCore(ref reader) ?? throw new RlpReaderException("Invalid ENR list structure.");

    /// <summary>
    /// Deserializes a record, returning false for malformed input. Does not verify its signature.
    /// </summary>
    public bool TryDeserialize(ref RlpReader reader, [NotNullWhen(true)] out NodeRecord? nodeRecord)
    {
        try
        {
            nodeRecord = DeserializeCore(ref reader);
            return nodeRecord is not null;
        }
        catch (Exception e) when (e is RlpReaderException or ArgumentException or InvalidOperationException or FormatException)
        {
            nodeRecord = null;
            return false;
        }
    }

    private static NodeRecord? DeserializeCore(ref RlpReader reader)
    {
        if (!reader.HasNext || !reader.IsNextSequence) return null;
        ReadOnlySpan<byte> recordRlp = reader.PeekNextItem();
        RlpReader fields = reader.EnterSequence(out int recordRlpLength);
        if (!fields.HasNext) return null;
        if (recordRlpLength > 300)
        {
            throw new RlpReaderException("RLP received for ENR is bigger than 300 bytes");
        }

        NodeRecord nodeRecord = new();
        ReadOnlySpan<byte> previousKey = default;

        ReadOnlySpan<byte> sigBytes = fields.DecodeByteArraySpan(RlpLimit.L65);
        if (!fields.HasNext) return null;
        Signature signature = new(sigBytes, 0);

        bool hasV4Id = false;
        ulong enrSequence = fields.DecodeULong();
        while (fields.HasNext)
        {
            ReadOnlySpan<byte> key = fields.DecodeByteArraySpan();
            if (!fields.HasNext) return null;
            if (previousKey.Length != 0 && key.SequenceCompareTo(previousKey) <= 0)
            {
                throw new RlpReaderException("ENR keys must be sorted and unique.");
            }
            previousKey = key;

            switch (key.Length)
            {
                case 2 when key.SequenceEqual(EnrContentKey.IdU8):
                    ReadOnlySpan<byte> id = fields.DecodeByteArraySpan();
                    if (!id.SequenceEqual("v4"u8))
                    {
                        throw new RlpReaderException("Unsupported ENR identity scheme.");
                    }

                    hasV4Id = true;
                    nodeRecord.SetEntry(IdEntry.Instance);
                    break;
                case 2 when key.SequenceEqual(EnrContentKey.IpU8):
                    {
                        ReadOnlySpan<byte> ipBytes = fields.DecodeByteArraySpan();
                        IPAddress address = new(ipBytes);
                        nodeRecord.SetEntry(new IpEntry(address));
                        break;
                    }
                case 3 when key.SequenceEqual(EnrContentKey.Ip6U8):
                    {
                        ReadOnlySpan<byte> ipBytes = fields.DecodeByteArraySpan();
                        IPAddress address = new(ipBytes);
                        nodeRecord.SetEntry(new Ip6Entry(address));
                        break;
                    }
                case 3 when key.SequenceEqual(EnrContentKey.EthU8):
                    if (!fields.IsNextSequence) return null;
                    ReadOnlySpan<byte> ethRlp = fields.PeekNextItem();
                    RlpReader eth = fields.EnterSequence(out int ethLength);
                    if (!eth.HasNext || !eth.IsNextSequence) return null;
                    RlpReader forkId = eth.EnterSequence(out int forkIdLength);
                    byte[] forkHash = forkId.DecodeByteArray(size: ForkId.ForkHashLength);
                    ulong next = forkId.DecodeULong();
                    eth.ExitSequence(forkId, forkIdLength);
                    bool hasAdditionalEthFields = eth.HasNext;
                    fields.ExitSequenceIgnoringRest(eth, ethLength);
                    byte[]? originalEthRlpValue = hasAdditionalEthFields
                        ? ethRlp.ToArray()
                        : null;
                    nodeRecord.SetEntry(new EthEntry(forkHash, next, originalEthRlpValue));
                    break;
                case 3 when key.SequenceEqual(EnrContentKey.TcpU8):
                    {
                        int tcpPort = fields.DecodePositiveInt();
                        nodeRecord.SetEntry(new TcpEntry(tcpPort));
                        break;
                    }
                case 4 when key.SequenceEqual(EnrContentKey.Tcp6U8):
                    {
                        int tcpPort = fields.DecodePositiveInt();
                        nodeRecord.SetEntry(new Tcp6Entry(tcpPort));
                        break;
                    }
                case 3 when key.SequenceEqual(EnrContentKey.UdpU8):
                    {
                        int udpPort = fields.DecodePositiveInt();
                        nodeRecord.SetEntry(new UdpEntry(udpPort));
                        break;
                    }
                case 4 when key.SequenceEqual(EnrContentKey.Udp6U8):
                    {
                        int udpPort = fields.DecodePositiveInt();
                        nodeRecord.SetEntry(new Udp6Entry(udpPort));
                        break;
                    }
                case 9 when key.SequenceEqual(EnrContentKey.SecP256k1U8):
                    ReadOnlySpan<byte> keyBytes = fields.DecodeByteArraySpan();
                    CompressedPublicKey reportedKey = new(keyBytes);
                    nodeRecord.SetEntry(new SecP256k1Entry(reportedKey));
                    break;
                default:
                    nodeRecord.SetEntry(new UnknownEntry(
                        Encoding.UTF8.GetString(key),
                        fields.ReadEncodedItem().ToArray()));
                    nodeRecord.Snap = true;
                    break;
            }
        }

        reader.ExitSequence(fields, recordRlpLength);
        if (!hasV4Id)
        {
            throw new RlpReaderException("ENR is missing id=v4.");
        }

        nodeRecord.EnrSequence = enrSequence;
        nodeRecord.Signature = signature;
        nodeRecord.OriginalRlp = recordRlp.ToArray();

        return nodeRecord;
    }

    /// <summary>
    /// Verifies if the public key recovered from the <see cref="Signature"/> of this record matches
    /// the one that is included in the <value>Secp256k1</value> entry.
    /// If the <value>Secp256k1</value> entry is missing then <value>false</value> is returned.
    /// </summary>
    /// <param name="nodeRecord">A <see cref="NodeRecord"/> for which to verify the signature.</param>
    /// <returns><value>True</value> if signature has a matching public key, otherwise <value>false</value></returns>
    /// <exception cref="Exception">Thrown when <see cref="Signature"/> is <value>null</value></exception>
    public bool Verify(NodeRecord nodeRecord)
    {
        if (nodeRecord.Signature is null)
        {
            throw new Exception("Cannot verify an ENR with an empty signature.");
        }

        CompressedPublicKey? reportedKey = nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1);
        if (reportedKey is null)
        {
            return false;
        }

        ValueHash256 contentHash = nodeRecord.ContentHash;
        CompressedPublicKey? publicKeyA =
            _ecdsa.RecoverCompressedPublicKey(nodeRecord.Signature, in contentHash);
        if (publicKeyA?.Equals(reportedKey) == true)
        {
            return true;
        }

        Signature sigB = new(nodeRecord.Signature.Bytes, 1);
        return _ecdsa.RecoverCompressedPublicKey(sigB, in contentHash)?.Equals(reportedKey) == true;
    }
}
