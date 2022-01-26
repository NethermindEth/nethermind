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
