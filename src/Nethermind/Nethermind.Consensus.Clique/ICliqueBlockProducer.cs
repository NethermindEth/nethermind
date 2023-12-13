// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Clique
{
    public interface ICliqueBlockProducer : IBlockProducer
    {
        void CastVote(Address signer, bool vote);
        void UncastVote(Address signer);
        void ProduceOnTopOf(Hash256 hash);
    }
}
