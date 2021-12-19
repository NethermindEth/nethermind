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
    private readonly IEcdsa _ecdsa;
    
    private readonly PrivateKey _privateKey;

    public NodeRecordSigner(IEcdsa? ethereumEcdsa, PrivateKey? privateKey)
    {
        _ecdsa = ethereumEcdsa ?? throw new ArgumentNullException(nameof(ethereumEcdsa));
        _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
    }

    public void Sign(NodeRecord nodeRecord)
    {
        KeccakRlpStream rlpStream = new();
        nodeRecord.EncodeContent(rlpStream);
        Signature signature = _ecdsa.Sign(_privateKey, new Keccak(rlpStream.GetHash()));
        nodeRecord.Seal(signature);
    }
    
    public CompressedPublicKey Verify(NodeRecord nodeRecord)
    {
        if (nodeRecord.Signature is null)
        {
            throw new Exception("Cannot verify an ENR with an empty signature.");
        }
        
        KeccakRlpStream rlpStream = new();
        nodeRecord.EncodeContent(rlpStream);
        
        // change after merging with changes from 868
        CompressedPublicKey publicKey = _ecdsa.RecoverCompressedPublicKey(nodeRecord.Signature!, new Keccak(rlpStream.GetHash()));
        return publicKey;
    }
}
