// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Ethash
{
    public interface IEthash
    {
        void HintRange(Guid guid, long start, long end);
        bool Validate(BlockHeader header);
        (Keccak MixHash, ulong Nonce) Mine(BlockHeader header, ulong? startNonce = null); // TODO: for now only with cache
    }
}
