// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.WireProtocol.Messages;

namespace Nethermind.Network.Discovery;

/// https://github.com/ethereum/devp2p/blob/master/discv5/discv5-wire.md#talkreq-request-0x05
internal class TalkReqAndRespHandler : ITalkReqAndRespHandler
{
    //Must send an empty response if no protocols are matched
    private static readonly byte[][] EmptyProtocolResponse = [[]];

    public byte[][]? HandleRequest(byte[] protocol, byte[] request)
    {
        //We currently don't advertise any supported protocols
        return EmptyProtocolResponse;
    }

    public byte[]? HandleResponse(byte[] response)
    {
        //We don't care about anything returned here at the moment
        return Array.Empty<byte>();
    }
}
