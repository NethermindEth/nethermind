// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class ContractCodesMessage : P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.ContractCodes;
        public override string Protocol { get; } = Contract.P2P.Protocol.Les;
        public long RequestId;
        public int BufferValue;
        public IDisposableReadOnlyList<byte[]> Codes;

        public ContractCodesMessage()
        {
        }

        public ContractCodesMessage(IDisposableReadOnlyList<byte[]> codes, long requestId, int bufferValue)
        {
            Codes = codes;
            RequestId = requestId;
            BufferValue = bufferValue;
        }

        public override void Dispose()
        {
            base.Dispose();
            Codes.Dispose();
        }
    }
}
