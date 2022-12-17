// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages
{
    public class NewPooledTransactionHashesMessage68 : P2PMessage
    {
        public const int MaxCount = 1024;

        public override int PacketType { get; } = Eth68MessageCode.NewPooledTransactionHashes;
        public override string Protocol { get; } = "eth";

        public readonly IReadOnlyList<byte> Types;
        public readonly IReadOnlyList<int> Sizes;
        public readonly IReadOnlyList<Keccak> Hashes;

        public NewPooledTransactionHashesMessage68(IReadOnlyList<byte> types, IReadOnlyList<int> sizes, IReadOnlyList<Keccak> hashes)
        {
            Types = types;
            Sizes = sizes;
            Hashes = hashes;
        }

        public override string ToString() => $"{nameof(NewPooledTransactionHashesMessage68)}({Hashes.Count})";
    }
}
