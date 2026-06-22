// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-778
/// </summary>
public class NodeRecordSigner(IEcdsa? ethereumEcdsa, PrivateKey? privateKey) : INodeRecordSigner
{
    private readonly IEcdsa _ecdsa = ethereumEcdsa ?? throw new ArgumentNullException(nameof(ethereumEcdsa));

    private readonly PrivateKey _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));

    /// <summary>
    /// Signs the node record with own private key.
    /// </summary>
    /// <param name="nodeRecord"></param>
    public void Sign(NodeRecord nodeRecord) => nodeRecord.Signature = _ecdsa.Sign(_privateKey, in nodeRecord.ContentHash.ValueHash256);

    /// <summary>
    /// Deserializes a <see cref="NodeRecord"/> from a <see cref="RlpReader"/>.
    /// </summary>
    /// <param name="reader">The RLP reader to read the serialized data from.</param>
    /// <returns>A deserialized <see cref="NodeRecord"/></returns>
    public NodeRecord Deserialize(ref RlpReader reader)
    {
        int startPosition = reader.Position;
        int recordRlpLength = reader.ReadSequenceLength();
        if (recordRlpLength > 300)
            throw new NetworkingException("RLP received for ENR is bigger than 300 bytes", NetworkExceptionType.Discovery);
        NodeRecord nodeRecord = new();

        ReadOnlySpan<byte> sigBytes = reader.DecodeByteArraySpan(RlpLimit.L65);
        Signature signature = new(sigBytes, 0);

        bool canVerify = true;
        long enrSequence = reader.DecodeLong();
        while (reader.Position < startPosition + recordRlpLength)
        {
            ReadOnlySpan<byte> key = reader.DecodeByteArraySpan();
            switch (key.Length)
            {
                case 2 when key.SequenceEqual(EnrContentKey.IdU8):
                    reader.SkipItem();
                    nodeRecord.SetEntry(IdEntry.Instance);
                    break;
                case 2 when key.SequenceEqual(EnrContentKey.IpU8):
                    ReadOnlySpan<byte> ipBytes = reader.DecodeByteArraySpan();
                    IPAddress address = new(ipBytes);
                    nodeRecord.SetEntry(new IpEntry(address));
                    break;
                case 3 when key.SequenceEqual(EnrContentKey.EthU8):
                    _ = reader.ReadSequenceLength();
                    _ = reader.ReadSequenceLength();
                    byte[] forkHash = reader.DecodeByteArray();
                    long nextBlock = reader.DecodeLong();
                    nodeRecord.SetEntry(new EthEntry(forkHash, nextBlock));
                    break;
                case 3 when key.SequenceEqual(EnrContentKey.TcpU8):
                    int tcpPort = reader.DecodePositiveInt();
                    nodeRecord.SetEntry(new TcpEntry(tcpPort));
                    break;
                case 3 when key.SequenceEqual(EnrContentKey.UdpU8):
                    int udpPort = reader.DecodePositiveInt();
                    nodeRecord.SetEntry(new UdpEntry(udpPort));
                    break;
                case 9 when key.SequenceEqual(EnrContentKey.SecP256k1U8):
                    ReadOnlySpan<byte> keyBytes = reader.DecodeByteArraySpan();
                    CompressedPublicKey reportedKey = new(keyBytes);
                    nodeRecord.SetEntry(new SecP256k1Entry(reportedKey));
                    break;
                default:
                    // snap
                    canVerify = false;
                    reader.SkipItem();
                    nodeRecord.Snap = true;
                    break;
            }
        }

        if (!canVerify)
        {
            reader.Position = startPosition;
            reader.ReadSequenceLength();
            reader.SkipItem(); // signature
            int noSigContentLength = reader.Length - reader.Position;
            int noSigSequenceLength = Rlp.LengthOfSequence(noSigContentLength);
            byte[] originalContent = new byte[noSigSequenceLength];
            RlpWriter writer = new(originalContent);
            writer.StartSequence(noSigContentLength);
            writer.WriteEncodedRlp(reader.Read(noSigContentLength));
            reader.Position = startPosition;
            nodeRecord.OriginalContentRlp = originalContent;
        }

        nodeRecord.EnrSequence = enrSequence;
        nodeRecord.Signature = signature;

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

        ValueHash256 contentHash;
        if (nodeRecord.OriginalContentRlp is not null)
        {
            contentHash = ValueKeccak.Compute(nodeRecord.OriginalContentRlp);
        }
        else
        {
            contentHash = nodeRecord.ContentHash;
        }

        CompressedPublicKey publicKeyA =
            _ecdsa.RecoverCompressedPublicKey(nodeRecord.Signature!, in contentHash)!;
        Signature sigB = new(nodeRecord.Signature!.Bytes, 1);
        CompressedPublicKey publicKeyB =
            _ecdsa.RecoverCompressedPublicKey(sigB, in contentHash)!;

        CompressedPublicKey? reportedKey =
            nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1);

        return publicKeyA.Equals(reportedKey) || publicKeyB.Equals(reportedKey);
    }
}
