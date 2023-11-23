// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class ByteCodesMessage : SnapMessageBase
    {
        public ByteCodesMessage(byte[][]? data)
        {
            Codes = data ?? Array.Empty<byte[]>();
        }

        public override int PacketType => SnapMessageCode.ByteCodes;

        public byte[][] Codes { get; }
    }
}
