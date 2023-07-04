// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Clique
{
    public interface ICliqueBlockProducer : IBlockProducer
    {
        void CastVote(Address signer, bool vote);
        void UncastVote(Address signer);
        void ProduceOnTopOf(Keccak hash);
    }
}
