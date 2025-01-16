// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;

namespace Nethermind.Consensus.Clique
{
    public class CliqueRpcModule(
        ICliqueBlockProducerRunner? cliqueBlockProducer,
        ISnapshotManager snapshotManager,
        IBlockFinder blockTree)
        : ICliqueRpcModule
    {
        private const string CannotVoteOnNonValidatorMessage = "Not a signer node - cannot vote";

        private readonly ISnapshotManager _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
        private readonly IBlockFinder _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));

        public bool ProduceBlock(Hash256 parentHash)
        {
            if (cliqueBlockProducer is null)
            {
                return false;
            }

            cliqueBlockProducer?.ProduceOnTopOf(parentHash);
            return true;
        }

        public void CastVote(Address signer, bool vote)
        {
            if (cliqueBlockProducer is null)
            {
                throw new InvalidOperationException(CannotVoteOnNonValidatorMessage);
            }

            cliqueBlockProducer.CastVote(signer, vote);
        }

        public void UncastVote(Address signer)
        {
            if (cliqueBlockProducer is null)
            {
                throw new InvalidOperationException(CannotVoteOnNonValidatorMessage);
            }

            cliqueBlockProducer.UncastVote(signer);
        }

        public Snapshot GetSnapshot(long? number = null)
        {
            Block head = _blockTree.Head;
            if (number is not null && head.Number != number)
            {
                head = _blockTree.FindBlock(number.Value);
            }
            return _snapshotManager.GetOrCreateSnapshot(head.Number, head.Hash);
        }

        public Snapshot GetSnapshot(Hash256 hash)
        {
            BlockHeader head = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return _snapshotManager.GetOrCreateSnapshot(head.Number, head.Hash);
        }

        public Address[] GetSigners()
        {
            Block head = _blockTree.Head;
            return _snapshotManager.GetOrCreateSnapshot(head.Number, head.Hash).Signers.Select(static s => s.Key).ToArray();
        }

        public Address[] GetSigners(long number)
        {
            BlockHeader header = _blockTree.FindHeader(number, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return _snapshotManager.GetOrCreateSnapshot(header.Number, header.Hash).Signers
                .Select(static s => s.Key).ToArray();
        }

        public string[] GetSignersAnnotated()
        {
            Block header = _blockTree.Head;
            return _snapshotManager.GetOrCreateSnapshot(header.Number, header.Hash).Signers
                .Select(static s => string.Concat(s.Key, $" ({KnownAddresses.GetDescription(s.Key)})")).ToArray();
        }

        public Address[] GetSigners(Hash256 hash)
        {
            BlockHeader header = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return _snapshotManager.GetOrCreateSnapshot(header.Number, header.Hash).Signers
                .Select(static s => s.Key).ToArray();
        }

        public string[] GetSignersAnnotated(Hash256 hash)
        {
            BlockHeader header = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return _snapshotManager.GetOrCreateSnapshot(header.Number, header.Hash).Signers
                .Select(static s => string.Concat(s.Key, $" ({KnownAddresses.GetDescription(s.Key)})")).ToArray();
        }

        public ResultWrapper<bool> clique_produceBlock(Hash256 parentHash) => ResultWrapper<bool>.Success(ProduceBlock(parentHash));

        public ResultWrapper<IReadOnlyDictionary<Address, bool>> clique_proposals() =>
            ResultWrapper<IReadOnlyDictionary<Address, bool>>.Success(cliqueBlockProducer?.GetProposals() ?? new Dictionary<Address, bool>());

        public ResultWrapper<Snapshot> clique_getSnapshot(long? number) => ResultWrapper<Snapshot>.Success(GetSnapshot(number));

        public ResultWrapper<Snapshot> clique_getSnapshotAtHash(Hash256 hash) => ResultWrapper<Snapshot>.Success(GetSnapshot(hash));

        public ResultWrapper<Address[]> clique_getSigners() => ResultWrapper<Address[]>.Success(GetSigners().ToArray());

        public ResultWrapper<Address[]> clique_getSignersAtHash(Hash256 hash) => ResultWrapper<Address[]>.Success(GetSigners(hash).ToArray());

        public ResultWrapper<Address[]> clique_getSignersAtNumber(long number) => ResultWrapper<Address[]>.Success(GetSigners(number).ToArray());

        public ResultWrapper<string[]> clique_getSignersAnnotated() => ResultWrapper<string[]>.Success(GetSignersAnnotated().ToArray());

        public ResultWrapper<string[]> clique_getSignersAtHashAnnotated(Hash256 hash) => ResultWrapper<string[]>.Success(GetSignersAnnotated(hash).ToArray());

        public ResultWrapper<Address?> clique_getBlockSigner(Hash256? hash)
        {
            if (hash is null)
            {
                return ResultWrapper<Address>.Fail($"Hash parameter cannot be null");
            }

            BlockHeader? header = _blockTree.FindHeader(hash);
            if (header is null)
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
