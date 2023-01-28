// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text;
using DotNetty.Buffers;
using DotNetty.Codecs.Base64;
using DotNetty.Common.Utilities;
using Nethermind.Core.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Dns;

public interface IEnrRecordParser
{
    NodeRecord ParseRecord(string nodeRecordText, IByteBuffer buffer);
    NodeRecord ParseRecord(string nodeRecordText);
}

public class EnrRecordParser : IEnrRecordParser
{
    private readonly INodeRecordSigner _nodeRecordSigner;

    public EnrRecordParser(INodeRecordSigner nodeRecordSigner)
    {
        _nodeRecordSigner = nodeRecordSigner;
    }

    public NodeRecord ParseRecord(string nodeRecordText)
    {
        IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer();
        try
        {
            return ParseRecord(nodeRecordText, buffer);
        }
        finally
        {
            buffer.Release();
        }
    }

    public NodeRecord ParseRecord(string nodeRecordText, IByteBuffer buffer)
    {
        void AddPadding(IByteBuffer byteBuffer, ICharSequence base64)
        {
            if (base64[^1] != '=')
            {
                int mod3 = base64.Count % 4;
                for (int i = 0; i < mod3; i++)
                {
                    byteBuffer.WriteString("=", Encoding.UTF8);
                }
            }
        }

        buffer.Clear();
        StringCharSequence base64Sequence = new(nodeRecordText, 4, nodeRecordText.Length - 4);
        buffer.WriteCharSequence(base64Sequence, Encoding.UTF8);
        AddPadding(buffer, base64Sequence);

        IByteBuffer base64Buffer = Base64.Decode(buffer, Base64Dialect.URL_SAFE);
        try
        {
            NettyRlpStream rlpStream = new(base64Buffer);
            return _nodeRecordSigner.Deserialize(rlpStream);
        }
        finally
        {
            base64Buffer.Release();
        }
    }
}
