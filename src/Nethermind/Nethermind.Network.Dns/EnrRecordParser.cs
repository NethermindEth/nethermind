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
    // EIP-778 limits a record to 300 bytes.
    private const int MaxRecordSize = 300;

    private readonly INodeRecordSigner _nodeRecordSigner = nodeRecordSigner;

    public NodeRecord ParseRecord(string nodeRecordText)
    {
        ReadOnlySpan<char> base64 = nodeRecordText.AsSpan(4);
        int maxLength = Base64Url.GetMaxDecodedLength(base64.Length);
        Span<byte> rlp = maxLength <= MaxRecordSize ? stackalloc byte[MaxRecordSize] : new byte[maxLength];
        RlpReader ctx = new(rlp[..Base64Url.DecodeFromChars(base64, rlp)]);
        NodeRecord result = _nodeRecordSigner.Deserialize(ref ctx);
        if (!_nodeRecordSigner.Verify(result))
        {
            throw new RlpException("Invalid ENR signature.");
        }

        return result;
    }
}
