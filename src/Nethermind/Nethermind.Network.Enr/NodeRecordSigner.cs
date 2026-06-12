// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
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
    /// Deserializes a <see cref="NodeRecord"/> from an <see cref="RlpStream"/>.
    /// </summary>
    /// <param name="rlpStream">A stream to read the serialized data from.</param>
    /// <returns>A deserialized <see cref="NodeRecord"/></returns>
    public NodeRecord Deserialize(RlpStream rlpStream)
    {
        Rlp.ValueDecoderContext ctx = new(rlpStream.Data.AsSpan().Slice(rlpStream.Position));
        NodeRecord result = Deserialize(ref ctx);
        rlpStream.Position += ctx.Position;
        return result;
    }

    /// <summary>
    /// Deserializes a <see cref="NodeRecord"/> from a <see cref="Rlp.ValueDecoderContext"/>.
    /// </summary>
    /// <param name="ctx">A value decoder context to read the serialized data from.</param>
    /// <returns>A deserialized <see cref="NodeRecord"/></returns>
    public NodeRecord Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        int startPosition = ctx.Position;
        int recordRlpLength = ctx.ReadSequenceLength();
        int checkPosition = ctx.Position + recordRlpLength;
        if (checkPosition - startPosition > 300)
            throw new RlpException("RLP received for ENR is bigger than 300 bytes");
        NodeRecord nodeRecord = new();
        ReadOnlySpan<byte> previousKey = default;

        ReadOnlySpan<byte> sigBytes = ctx.DecodeByteArraySpan(RlpLimit.L65);
        Signature signature = new(sigBytes, 0);
        int contentStartPosition = ctx.Position;

        bool hasV4Id = false;
        ulong enrSequence = ctx.DecodeULong();
        while (ctx.Position < checkPosition)
        {
            ReadOnlySpan<byte> key = ctx.DecodeByteArraySpan();
            if (previousKey.Length != 0 && key.SequenceCompareTo(previousKey) <= 0)
            {
                throw new RlpException("ENR keys must be sorted and unique.");
            }
            previousKey = key;

            switch (key.Length)
            {
                case 2 when key.SequenceEqual(EnrContentKey.IdU8):
                    ReadOnlySpan<byte> id = ctx.DecodeByteArraySpan();
                    if (!id.SequenceEqual("v4"u8))
                    {
                        throw new RlpException("Unsupported ENR identity scheme.");
                    }

                    hasV4Id = true;
                    nodeRecord.SetEntry(IdEntry.Instance);
                    break;
                case 2 when key.SequenceEqual(EnrContentKey.IpU8):
                    {
                        ReadOnlySpan<byte> ipBytes = ctx.DecodeByteArraySpan();
                        IPAddress address = new(ipBytes);
                        nodeRecord.SetEntry(new IpEntry(address));
                        break;
                    }
                case 3 when key.SequenceEqual(EnrContentKey.Ip6U8):
                    {
                        ReadOnlySpan<byte> ipBytes = ctx.DecodeByteArraySpan();
                        IPAddress address = new(ipBytes);
                        nodeRecord.SetEntry(new Ip6Entry(address));
                        break;
                    }
                case 3 when key.SequenceEqual(EnrContentKey.EthU8):
                    int ethEntryLength = ctx.ReadSequenceLength();
                    int ethEntryCheck = ctx.Position + ethEntryLength;
                    int forkIdLength = ctx.ReadSequenceLength();
                    int forkIdCheck = ctx.Position + forkIdLength;
                    byte[] forkHash = ctx.DecodeByteArray(RlpLimit.L4, EthEntry.ForkHashLength);
                    ulong nextBlock = ctx.DecodeULong();
                    ctx.Check(forkIdCheck);

                    while (ctx.Position < ethEntryCheck)
                    {
                        ctx.SkipItem();
                    }
                    ctx.Check(ethEntryCheck);
                    nodeRecord.SetEntry(new EthEntry(forkHash, nextBlock));
                    break;
                case 3 when key.SequenceEqual(EnrContentKey.TcpU8):
                    {
                        int tcpPort = ctx.DecodePositiveInt();
                        nodeRecord.SetEntry(new TcpEntry(tcpPort));
                        break;
                    }
                case 4 when key.SequenceEqual(EnrContentKey.Tcp6U8):
                    {
                        int tcpPort = ctx.DecodePositiveInt();
                        nodeRecord.SetEntry(new Tcp6Entry(tcpPort));
                        break;
                    }
                case 3 when key.SequenceEqual(EnrContentKey.UdpU8):
                    {
                        int udpPort = ctx.DecodePositiveInt();
                        nodeRecord.SetEntry(new UdpEntry(udpPort));
                        break;
                    }
                case 4 when key.SequenceEqual(EnrContentKey.Udp6U8):
                    {
                        int udpPort = ctx.DecodePositiveInt();
                        nodeRecord.SetEntry(new Udp6Entry(udpPort));
                        break;
                    }
                case 9 when key.SequenceEqual(EnrContentKey.SecP256k1U8):
                    ReadOnlySpan<byte> keyBytes = ctx.DecodeByteArraySpan();
                    CompressedPublicKey reportedKey = new(keyBytes);
                    nodeRecord.SetEntry(new SecP256k1Entry(reportedKey));
                    break;
                default:
                    int valueStart = ctx.Position;
                    ctx.SkipItem();
                    int valueLength = ctx.Position - valueStart;
                    nodeRecord.SetEntry(new UnknownEntry(
                        Encoding.UTF8.GetString(key),
                        ctx.Data.Slice(valueStart, valueLength).ToArray()));
                    nodeRecord.Snap = true;
                    break;
            }
        }

        ctx.Check(checkPosition);
        if (!hasV4Id)
        {
            throw new RlpException("ENR is missing id=v4.");
        }

        int endPosition = ctx.Position;
        int originalContentLength = endPosition - contentStartPosition;
        byte[] originalContent = new byte[Rlp.LengthOfSequence(originalContentLength)];
        RlpStream originalContentStream = new(originalContent);
        originalContentStream.StartSequence(originalContentLength);
        originalContentStream.Write(ctx.Data.Slice(contentStartPosition, originalContentLength));

        nodeRecord.EnrSequence = enrSequence;
        nodeRecord.Signature = signature;
        nodeRecord.OriginalContentRlp = originalContent;
        nodeRecord.OriginalRlp = ctx.Data.Slice(startPosition, endPosition - startPosition).ToArray();

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

        ValueHash256 contentHash = nodeRecord.ContentHash;

        CompressedPublicKey? publicKeyA =
            _ecdsa.RecoverCompressedPublicKey(nodeRecord.Signature!, in contentHash);
        Signature sigB = new(nodeRecord.Signature!.Bytes, 1);
        CompressedPublicKey? publicKeyB =
            _ecdsa.RecoverCompressedPublicKey(sigB, in contentHash);

        CompressedPublicKey? reportedKey =
            nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1);

        return publicKeyA?.Equals(reportedKey) == true || publicKeyB?.Equals(reportedKey) == true;
    }
}
