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

        /// <summary>Mines a nonce for <paramref name="header"/> like <see cref="Mine(BlockHeader, ulong?)"/>, and can stop early
        /// when <paramref name="cancellationToken"/> is cancelled.</summary>
        /// <remarks>The default calls <see cref="Mine(BlockHeader, ulong?)"/> and ignores the token; implementations that can stop
        /// mining override it and throw <see cref="OperationCanceledException"/> when cancelled.</remarks>
        (Hash256 MixHash, ulong Nonce) Mine(BlockHeader header, ulong? startNonce, CancellationToken cancellationToken) => Mine(header, startNonce);
    }
}
