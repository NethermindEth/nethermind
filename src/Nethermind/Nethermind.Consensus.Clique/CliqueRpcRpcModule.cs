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

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;

namespace Nethermind.Consensus.Clique
{
    public class CliqueRpcRpcModule : ICliqueRpcModule
    {
        private const string CannotVoteOnNonValidatorMessage = "Not a signer node - cannot vote";

        private readonly ICliqueBlockProducer? _cliqueBlockProducer;
        private readonly ISnapshotManager _snapshotManager;
        private readonly IBlockFinder _blockTree;

        public CliqueRpcRpcModule(ICliqueBlockProducer? cliqueBlockProducer, ISnapshotManager snapshotManager, IBlockFinder blockTree)
        {
            _cliqueBlockProducer = cliqueBlockProducer;
            _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }

        public bool ProduceBlock(Keccak parentHash)
        {
            if (_cliqueBlockProducer == null)
            {
                return false;
            }

            _cliqueBlockProducer?.ProduceOnTopOf(parentHash);
            return true;
        }

        public void CastVote(Address signer, bool vote)
        {
            if (_cliqueBlockProducer == null)
            {
                throw new InvalidOperationException(CannotVoteOnNonValidatorMessage);
            }

            _cliqueBlockProducer.CastVote(signer, vote);
        }

        public void UncastVote(Address signer)
        {
            if (_cliqueBlockProducer == null)
            {
                throw new InvalidOperationException(CannotVoteOnNonValidatorMessage);
            }

            _cliqueBlockProducer.UncastVote(signer);
        }

        public Snapshot GetSnapshot()
        {
            Block head = _blockTree.Head;
            return _snapshotManager.GetOrCreateSnapshot(head.Number, head.Hash);
        }

        public Snapshot GetSnapshot(Keccak hash)
        {
            BlockHeader head = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return _snapshotManager.GetOrCreateSnapshot(head.Number, head.Hash);
        }

        public Address[] GetSigners()
        {
            Block head = _blockTree.Head;
            return _snapshotManager.GetOrCreateSnapshot(head.Number, head.Hash).Signers.Select(s => s.Key).ToArray();
        }

        public Address[] GetSigners(long number)
        {
            BlockHeader header = _blockTree.FindHeader(number, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return _snapshotManager.GetOrCreateSnapshot(header.Number, header.Hash).Signers
                .Select(s => s.Key).ToArray();
        }

        public string[] GetSignersAnnotated()
        {
            Block header = _blockTree.Head;
            return _snapshotManager.GetOrCreateSnapshot(header.Number, header.Hash).Signers
                .Select(s => string.Concat(s.Key, $" ({KnownAddresses.GetDescription(s.Key)})")).ToArray();
        }

        public Address[] GetSigners(Keccak hash)
        {
            BlockHeader header = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return _snapshotManager.GetOrCreateSnapshot(header.Number, header.Hash).Signers
                .Select(s => s.Key).ToArray();
        }

        public string[] GetSignersAnnotated(Keccak hash)
        {
            BlockHeader header = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return _snapshotManager.GetOrCreateSnapshot(header.Number, header.Hash).Signers
                .Select(s => string.Concat(s.Key, $" ({KnownAddresses.GetDescription(s.Key)})")).ToArray();
        }

        public ResultWrapper<bool> clique_produceBlock(Keccak parentHash)
        {
            return ResultWrapper<bool>.Success(ProduceBlock(parentHash));
        }

        public ResultWrapper<Snapshot> clique_getSnapshot()
        {
            return ResultWrapper<Snapshot>.Success(GetSnapshot());
        }

        public ResultWrapper<Snapshot> clique_getSnapshotAtHash(Keccak hash)
        {
            return ResultWrapper<Snapshot>.Success(GetSnapshot(hash));
        }

        public ResultWrapper<Address[]> clique_getSigners()
        {
            return ResultWrapper<Address[]>.Success(GetSigners().ToArray());
        }

        public ResultWrapper<Address[]> clique_getSignersAtHash(Keccak hash)
        {
            return ResultWrapper<Address[]>.Success(GetSigners(hash).ToArray());
        }

        public ResultWrapper<Address[]> clique_getSignersAtNumber(long number)
        {
            return ResultWrapper<Address[]>.Success(GetSigners(number).ToArray());
        }

        public ResultWrapper<string[]> clique_getSignersAnnotated()
        {
            return ResultWrapper<string[]>.Success(GetSignersAnnotated().ToArray());
        }

        public ResultWrapper<string[]> clique_getSignersAtHashAnnotated(Keccak hash)
        {
            return ResultWrapper<string[]>.Success(GetSignersAnnotated(hash).ToArray());
        }
        
        public ResultWrapper<Address?> clique_getBlockSigner(Keccak? hash)
        {
            if (hash is null)
            {
                return ResultWrapper<Address>.Fail($"Hash parameter cannot be null");    
            }
            
            BlockHeader? header = _blockTree.FindHeader(hash);
            if (header == null)
            {
                return ResultWrapper<Address>.Fail($"Could not find block with hash {hash}");    
            }
            
            header.Author ??= _snapshotManager.GetBlockSealer(header);
            return ResultWrapper<Address>.Success(header.Author);
        }

        public ResultWrapper<bool> clique_propose(Address signer, bool vote)
        {
            try
            {
                CastVote(signer, vote);
            }
            catch (Exception ex)
            {
                return ResultWrapper<bool>.Fail($"Unable to cast vote: {ex}", ErrorCodes.InternalError);
            }

            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<bool> clique_discard(Address signer)
        {
            try
            {
                UncastVote(signer);
            }
            catch (Exception)
            {
                return ResultWrapper<bool>.Fail("Unable to uncast vote", ErrorCodes.InternalError);
            }

            return ResultWrapper<bool>.Success(true);
        }
    }
}
