// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
