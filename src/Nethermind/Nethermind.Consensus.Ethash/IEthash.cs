// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Ethash
{
    public interface IEthash
    {
        void HintRange(Guid guid, ulong start, ulong end);
        bool Validate(BlockHeader header);
        (Hash256 MixHash, ulong Nonce) Mine(BlockHeader header, ulong? startNonce = null); // TODO: for now only with cache

        // The default ignores the token; implementations that can stop mining when cancelled override it
        (Hash256 MixHash, ulong Nonce) Mine(BlockHeader header, ulong? startNonce, CancellationToken cancellationToken) => Mine(header, startNonce);
    }
}
