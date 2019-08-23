/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Clique
{
    public class CliqueBridge : ICliqueBridge
    {
        private const string CannotVoteOnNonValidatorMessage = "Not a signer node - cannot vote";
        
        private readonly ICliqueBlockProducer _cliqueBlockProducer;
        private readonly ISnapshotManager _snapshotManager;
        private readonly IBlockTree _blockTree;

        public CliqueBridge(ICliqueBlockProducer cliqueBlockProducer, ISnapshotManager snapshotManager, IBlockTree blockTree)
        {
            _cliqueBlockProducer = cliqueBlockProducer;
            _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
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
            BlockHeader head = _blockTree.Head;
            return _snapshotManager.GetOrCreateSnapshot(head.Number, head.Hash);
        }

        public Snapshot GetSnapshot(Keccak hash)
        {
            BlockHeader head = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return _snapshotManager.GetOrCreateSnapshot(head.Number, head.Hash);
        }

        public static Address[] ExtractSigners(BlockHeader blockHeader)
        {
            Span<byte> signersData = blockHeader.ExtraData.AsSpan().Slice(Clique.ExtraVanityLength, blockHeader.ExtraData.Length - Clique.ExtraSealLength - Clique.ExtraVanityLength);
            Address[] signers = new Address[signersData.Length / Address.ByteLength];
            for (int i = 0; i < signers.Length; i++)
            {
                signers[i] = new Address(signersData.Slice(i * 20, 20).ToArray());
            }

            return signers;
        }

        public Address[] GetSigners()
        {
            BlockHeader head = _blockTree.Head;
            return _snapshotManager.GetOrCreateSnapshot(head.Number, head.Hash).Signers.Select(s => s.Key).ToArray();
        }

        public Address[] GetSigners(long number)
        {
            BlockHeader header = _blockTree.FindHeader(number, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return _snapshotManager.GetOrCreateSnapshot(header.Number, header.Hash).Signers.Select(s => s.Key).ToArray();
        }

        public string[] GetSignersAnnotated()
        {
            BlockHeader header = _blockTree.Head;
            return _snapshotManager.GetOrCreateSnapshot(header.Number, header.Hash).Signers.Select(s => string.Concat(s.Key, $" ({KnownAddresses.GetDescription(s.Key)})")).ToArray();
        }
        
        public Address[] GetSigners(Keccak hash)
        {
            BlockHeader header = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return _snapshotManager.GetOrCreateSnapshot(header.Number, header.Hash).Signers.Select(s => s.Key).ToArray();
        }
        
        public string[] GetSignersAnnotated(Keccak hash)
        {
            BlockHeader header = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return _snapshotManager.GetOrCreateSnapshot(header.Number, header.Hash).Signers.Select(s => string.Concat(s.Key, $" ({KnownAddresses.GetDescription(s.Key)})")).ToArray();
        }
    }
}