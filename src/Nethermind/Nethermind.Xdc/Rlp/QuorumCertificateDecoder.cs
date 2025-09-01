// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Rlp;
internal class QuorumCertificateDecoder : IRlpValueDecoder<QuorumCertificate>, IRlpStreamDecoder<QuorumCertificate>
{
    public QuorumCertificate Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        throw new NotImplementedException();
    }

    public QuorumCertificate Decode(ref Serialization.Rlp.Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        throw new NotImplementedException();
    }

    public void Encode(RlpStream stream, QuorumCertificate item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        throw new NotImplementedException();
    }

    public int GetLength(QuorumCertificate item, RlpBehaviors rlpBehaviors)
    {
        throw new NotImplementedException();
    }
}
