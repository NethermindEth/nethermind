// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class NewBlockHashesMessage(params (Hash256, long)[] blockHashes) : P2PMessage
    {
        public override int PacketType => Eth62MessageCode.NewBlockHashes;
        public override string Protocol => "eth";

        public (Hash256, long)[] BlockHashes { get; } = blockHashes;

        public override string ToString()
        {
            return $"{nameof(NewBlockHashesMessage)}({BlockHashes?.Length ?? 0})";
        }
    }
}
