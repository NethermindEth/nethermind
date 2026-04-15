// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Consensus
{
    public class NethDevSealEngine(Address? address = null) : ISealer, ISealValidator
    {
        public Task<Block> SealBlock(Block block, CancellationToken cancellationToken)
        {
            block.Header.MixHash = Keccak.Zero;
            block.Header.Hash = block.CalculateHash();
            return Task.FromResult(block);
        }

        public bool CanSeal(long blockNumber, Hash256 parentHash) => true;

        public Address Address { get; } = address ?? Address.Zero;

        public bool ValidateParams(BlockHeader? parent, BlockHeader? header, bool isUncle = false) => true;

        public bool ValidateSeal(BlockHeader? header, bool force) => true;
    }
}
