// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class NewPooledTransactionHashesMessage : HashesMessage
    {
        public const int MaxCount = 2048;

        public override int PacketType { get; } = Eth65MessageCode.NewPooledTransactionHashes;
        public override string Protocol { get; } = "eth";

        public NewPooledTransactionHashesMessage(IReadOnlyList<Keccak> hashes)
            : base(hashes)
        {
        }

        public override string ToString() => $"{nameof(NewPooledTransactionHashesMessage)}({Hashes?.Count})";
    }
}
