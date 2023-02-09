// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus
{
    public class SealEngine : ISealEngine
    {
        private readonly ISealer _sealer;
        private readonly ISealValidator _sealValidator;

        public SealEngine(ISealer? sealer, ISealValidator? sealValidator)
        {
            _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
        }

        public Task<Block> SealBlock(Block block, CancellationToken cancellationToken) =>
            _sealer.SealBlock(block, cancellationToken);

        public bool CanSeal(long blockNumber, Keccak parentHash) =>
            _sealer.CanSeal(blockNumber, parentHash);

        public Address Address => _sealer.Address;

        public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle) =>
            _sealValidator.ValidateParams(parent, header, isUncle);

        public bool ValidateSeal(BlockHeader header, bool force) =>
            _sealValidator.ValidateSeal(header, force);
    }
}
