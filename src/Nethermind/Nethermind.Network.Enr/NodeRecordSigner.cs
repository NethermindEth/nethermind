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
public class NodeRecordSigner
{
    private readonly IEcdsa _ecdsa;

    private readonly PrivateKey _privateKey;

    public NodeRecordSigner(IEcdsa? ethereumEcdsa, PrivateKey? privateKey)
    {
        _ecdsa = ethereumEcdsa ?? throw new ArgumentNullException(nameof(ethereumEcdsa));
        _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
    }

    public void Sign(NodeRecord nodeRecord)
    {
        Signature signature = _ecdsa.Sign(_privateKey, nodeRecord.ContentHash);
        nodeRecord.Seal(signature);
    }

    public NodeRecord Deserialize(RlpStream rlpStream)
    {
        int startPosition = rlpStream.Position;
        int recordRlpLength = rlpStream.ReadSequenceLength();

        NodeRecord nodeRecord = new();

        // TODO: may want to move this deserialization logic to something reusable
        ReadOnlySpan<byte> sigBytes = rlpStream.DecodeByteArraySpan();
        Signature signature = new(sigBytes, 0);

        bool canVerify = true;
        int enrSequence = rlpStream.DecodeInt();
        while (rlpStream.Position < startPosition + recordRlpLength)
        {
            string key = rlpStream.DecodeString();
            switch (key)
            {
                case EnrContentKey.Eth:
                    _ = rlpStream.ReadSequenceLength();
                    _ = rlpStream.ReadSequenceLength();
                    byte[] forkHash = rlpStream.DecodeByteArray();
                    int nextBlock = rlpStream.DecodeInt();
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
                    CompressedPublicKey? reportedKey = new(keyBytes);
                    nodeRecord.SetEntry(new Secp256K1Entry(reportedKey));
                    break;
                // snap
                default:
                    canVerify = false;
                    rlpStream.SkipItem();
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
            RlpStream originalContentStream = new (originalContent);
            originalContentStream.StartSequence(noSigContentLength);
            originalContentStream.Write(rlpStream.Read(noSigContentLength));
            rlpStream.Position = startPosition;
            nodeRecord.OriginalContentRlp = originalContentStream.Data!;
        }

        nodeRecord.EnrSequence = enrSequence;
        nodeRecord.Seal(signature);

        return nodeRecord;
    }

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
        Signature sigB = new (nodeRecord.Signature!.Bytes, 1);
        CompressedPublicKey publicKeyB =
            _ecdsa.RecoverCompressedPublicKey(sigB, contentHash);
        
        CompressedPublicKey? reportedKey =
            nodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.Secp256K1);
        
        return publicKeyA.Equals(reportedKey) || publicKeyB.Equals(reportedKey);
    }
}
