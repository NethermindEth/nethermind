// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Text;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Dns;

public interface IEnrRecordParser
{
    /// <summary>
    /// Parses and signature-verifies an ENR.
    /// </summary>
    NodeRecord ParseRecord(string nodeRecordText);
}

public class EnrRecordParser(INodeRecordSigner nodeRecordSigner) : IEnrRecordParser
{
    private readonly INodeRecordSigner _nodeRecordSigner = nodeRecordSigner;

    public NodeRecord ParseRecord(string nodeRecordText)
    {
        byte[] rlp = Base64Url.DecodeFromChars(nodeRecordText.AsSpan(4));
        RlpReader ctx = new(rlp);
        NodeRecord result = _nodeRecordSigner.Deserialize(ref ctx);
        if (!_nodeRecordSigner.Verify(result))
        {
            throw new RlpException("Invalid ENR signature.");
        }

        return result;
    }
}
