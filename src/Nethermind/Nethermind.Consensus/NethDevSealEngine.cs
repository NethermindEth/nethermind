// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Consensus
{
    public class NethDevSealEngine : ISealer, ISealValidator
    {
        public NethDevSealEngine(Address address = null)
        {
            Address = address ?? Address.Zero;
        }

        public Task<Block> SealBlock(Block block, CancellationToken cancellationToken)
        {
            block.Header.MixHash = Keccak.Zero;
            block.Header.Hash = block.CalculateHash();
            return Task.FromResult(block);
        }

        public bool CanSeal(long blockNumber, Keccak parentHash)
        {
            return true;
        }

        public Address Address { get; }

        public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
        {
            return true;
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            return true;
        }
    }
}
