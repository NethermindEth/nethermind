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

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-778
/// </summary>
public class NodeRecordSigner
{
    private readonly IEthereumEcdsa _ethereumEcdsa;
    
    private readonly PrivateKey _privateKey;

    public NodeRecordSigner(IEthereumEcdsa? ethereumEcdsa, PrivateKey? privateKey)
    {
        _ethereumEcdsa = ethereumEcdsa ?? throw new ArgumentNullException(nameof(ethereumEcdsa));
        _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
    }

    private Signature Sign(NodeRecord nodeRecord)
    {
        KeccakRlpStream rlpStream = new();
        nodeRecord.Encode(rlpStream);

        return _ethereumEcdsa.Sign(_privateKey, new Keccak(rlpStream.GetHash()));
    }
    
    public string GetEnrString(NodeRecord nodeRecord)
    {
        Signature signature = Sign(nodeRecord);
        int rlpLength = nodeRecord.GetRlpLengthWithSignature();
        RlpStream rlpStream = new(rlpLength);
        nodeRecord.Encode(rlpStream, signature);
        byte[] rlpData = rlpStream.Data!;
        
        // TODO: verify if the Base64 URL safe alphabet is used (just via the test case)
        // https://tools.ietf.org/html/rfc4648#section-5
        return string.Concat("enr:", Convert.ToBase64String(rlpData));
    }
}
