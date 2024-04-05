// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class ByteCodesMessage(IOwnedReadOnlyList<byte[]>? data) : SnapMessageBase
    {
        public override int PacketType => SnapMessageCode.ByteCodes;

        public IOwnedReadOnlyList<byte[]> Codes { get; } = data ?? ArrayPoolList<byte[]>.Empty();

        public override void Dispose()
        {
            base.Dispose();
            Codes.Dispose();
        }
    }
}
