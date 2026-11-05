// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class ByteCodesMessage(IByteArrayList? data) : SnapMessageBase
    {
        public override int PacketType => Snap1MessageCode.ByteCodes;

        public IByteArrayList Codes { get; } = data ?? EmptyByteArrayList.Instance;

        public override void Dispose()
        {
            base.Dispose();
            Codes.Dispose();
        }
    }
}
