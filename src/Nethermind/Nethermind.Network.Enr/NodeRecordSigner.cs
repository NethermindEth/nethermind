//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-778
/// </summary>
public class NodeRecordSigner : INodeRecordSigner
{
    private readonly IEcdsa _ecdsa;

    private readonly PrivateKey _privateKey;

    public NodeRecordSigner(IEcdsa? ethereumEcdsa, PrivateKey? privateKey)
    {
        _ecdsa = ethereumEcdsa ?? throw new ArgumentNullException(nameof(ethereumEcdsa));
        _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
    }

    /// <summary>
    /// Signs the node record with own private key.
    /// </summary>
    /// <param name="nodeRecord"></param>
    public void Sign(NodeRecord nodeRecord)
    {
        nodeRecord.Signature = _ecdsa.Sign(_privateKey, nodeRecord.ContentHash);
    }

    /// <summary>
    /// Deserializes a <see cref="NodeRecord"/> from an <see cref="RlpStream"/>.
    /// </summary>
    /// <param name="rlpStream">A stream to read the serialized data from.</param>
    /// <returns>A deserialized <see cref="NodeRecord"/></returns>
    public NodeRecord Deserialize(RlpStream rlpStream)
    {
        int startPosition = rlpStream.Position;
        int recordRlpLength = rlpStream.ReadSequenceLength();
        if (recordRlpLength > 300)
            throw new NetworkingException("RLP recieved for ENR is bigger than 300 bytes", NetworkExceptionType.Discovery);
        NodeRecord nodeRecord = new();

        ReadOnlySpan<byte> sigBytes = rlpStream.DecodeByteArraySpan();
        Signature signature = new(sigBytes, 0);

        bool canVerify = true;
        long enrSequence = rlpStream.DecodeLong();
        while (rlpStream.Position < startPosition + recordRlpLength)
        {
            string key = rlpStream.DecodeString();
            switch (key)
            {
                case EnrContentKey.Eth:
                    _ = rlpStream.ReadSequenceLength();
                    _ = rlpStream.ReadSequenceLength();
                    byte[] forkHash = rlpStream.DecodeByteArray();
                    long nextBlock = rlpStream.DecodeLong();
                    nodeRecord.SetEntry(new EthEntry(forkHash, nextBlock));
                    break;
                case EnrContentKey.Id:
                    rlpStream.SkipItem();
                    nodeRecord.SetEntry(IdEntry.Instance);
                    break;
                case EnrContentKey.Ip:
                    ReadOnlySpan<byte> ipBytes = rlpStream.DecodeByteArraySpan();
                    IPAddress address = new(ipBytes);
                    nodeRecord.SetEntry(new IpEntry(address));
                    break;
                case EnrContentKey.Tcp:
                    int tcpPort = rlpStream.DecodeInt();
                    nodeRecord.SetEntry(new TcpEntry(tcpPort));
                    break;
                case EnrContentKey.Udp:
                    int udpPort = rlpStream.DecodeInt();
                    nodeRecord.SetEntry(new UdpEntry(udpPort));
                    break;
                case EnrContentKey.Secp256K1:
                    ReadOnlySpan<byte> keyBytes = rlpStream.DecodeByteArraySpan();
                    CompressedPublicKey reportedKey = new(keyBytes);
                    nodeRecord.SetEntry(new Secp256K1Entry(reportedKey));
                    break;
                // snap
                default:
                    canVerify = false;
                    rlpStream.SkipItem();
                    nodeRecord.Snap = true;
                    break;
            }
        }

        if (!canVerify)
        {
            rlpStream.Position = startPosition;
            rlpStream.ReadSequenceLength();
            rlpStream.SkipItem(); // signature
            int noSigContentLength = rlpStream.Length - rlpStream.Position;
            int noSigSequenceLength = Rlp.LengthOfSequence(noSigContentLength);
            byte[] originalContent = new byte[noSigSequenceLength];
            RlpStream originalContentStream = new(originalContent);
            originalContentStream.StartSequence(noSigContentLength);
            originalContentStream.Write(rlpStream.Read(noSigContentLength));
            rlpStream.Position = startPosition;
            nodeRecord.OriginalContentRlp = originalContentStream.Data!;
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

        Keccak contentHash;
        if (nodeRecord.OriginalContentRlp is not null)
        {
            contentHash = Keccak.Compute(nodeRecord.OriginalContentRlp);
        }
        else
        {
            contentHash = nodeRecord.ContentHash;
        }

        CompressedPublicKey publicKeyA =
            _ecdsa.RecoverCompressedPublicKey(nodeRecord.Signature!, contentHash);
        Signature sigB = new(nodeRecord.Signature!.Bytes, 1);
        CompressedPublicKey publicKeyB =
            _ecdsa.RecoverCompressedPublicKey(sigB, contentHash);

        CompressedPublicKey? reportedKey =
            nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.Secp256K1);

        return publicKeyA.Equals(reportedKey) || publicKeyB.Equals(reportedKey);
    }
}
