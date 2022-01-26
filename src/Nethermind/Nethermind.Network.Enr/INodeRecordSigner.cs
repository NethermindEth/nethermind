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
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

public interface INodeRecordSigner
{
    /// <summary>
    /// Signs the node record with own private key.
    /// </summary>
    /// <param name="nodeRecord"></param>
    void Sign(NodeRecord nodeRecord);

    /// <summary>
    /// Deserializes a <see cref="NodeRecord"/> from an <see cref="RlpStream"/>.
    /// </summary>
    /// <param name="rlpStream">A stream to read the serialized data from.</param>
    /// <returns>A deserialized <see cref="NodeRecord"/></returns>
    NodeRecord Deserialize(RlpStream rlpStream);

    /// <summary>
    /// Verifies if the public key recovered from the <see cref="Signature"/> of this record matches
    /// the one that is included in the <value>Secp256k1</value> entry.
    /// If the <value>Secp256k1</value> entry is missing then <value>false</value> is returned.
    /// </summary>
    /// <param name="nodeRecord">A <see cref="NodeRecord"/> for which to verify the signature.</param>
    /// <returns><value>True</value> if signature has a matching public key, otherwise <value>false</value></returns>
    /// <exception cref="Exception">Thrown when <see cref="Signature"/> is <value>null</value></exception>
    bool Verify(NodeRecord nodeRecord);
}
