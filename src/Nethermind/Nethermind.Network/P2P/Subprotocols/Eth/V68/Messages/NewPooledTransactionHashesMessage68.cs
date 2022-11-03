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

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages
{
    public class NewPooledTransactionHashesMessage68 : P2PMessage
    {
        public const int MaxCount = 2048;

        public override int PacketType { get; } = Eth68MessageCode.NewPooledTransactionHashes;
        public override string Protocol { get; } = "eth";

        public readonly IReadOnlyList<TxType> Types;
        public readonly IReadOnlyList<int> Sizes;
        public readonly IReadOnlyList<Keccak> Hashes;

        public NewPooledTransactionHashesMessage68(IReadOnlyList<TxType> types, IReadOnlyList<int> sizes, IReadOnlyList<Keccak> hashes)
        {
            Types = types;
            Sizes = sizes;
            Hashes = hashes;
        }

        public override string ToString() => $"{nameof(NewPooledTransactionHashesMessage68)}({Hashes.Count})";
    }
}
