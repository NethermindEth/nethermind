// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test.Helpers;

internal class TestXdcSealer(CandidateContainer candidateContainer, ISigner signer, IXdcConsensusContext consensusContext, IEpochSwitchManager epochSwitchManager, ISnapshotManager snapshotManager, ISpecProvider specProvider) : ISealer
{
    public Address Address => signer.Address;
    private readonly XdcSealer _xdcSealer = new XdcSealer(signer);
    private readonly Signer signer = (Signer)signer;

    public bool CanSeal(long blockNumber, Hash256 parentHash)
    {
        return true;
    }

    public Task<Block> SealBlock(Block block, CancellationToken cancellationToken)
    {
        var header = (XdcBlockHeader)block.Header;
        IXdcReleaseSpec headSpec = specProvider.GetXdcSpec(header, consensusContext.CurrentRound);
        var leader = GetLeaderAddress(header, consensusContext.CurrentRound, headSpec);
        signer.SetSigner(candidateContainer.MasternodeCandidates.First(k => k.Address == leader));
        block.Header.Author = leader;
        block.Header.Beneficiary = leader;
        return _xdcSealer.SealBlock(block, cancellationToken);
    }

    private Address GetLeaderAddress(XdcBlockHeader currentHead, ulong round, IXdcReleaseSpec spec)
    {
        Address[] masternodes;
        if (epochSwitchManager.IsEpochSwitchAtRound(round, currentHead))
        {
            //TODO calculate master nodes based on the current round
            (masternodes, _) = snapshotManager.CalculateNextEpochMasternodes(currentHead.Number, currentHead.ParentHash!, spec);
        }
        else
        {
            var epochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(currentHead);
            masternodes = epochSwitchInfo!.Masternodes;
        }

        int currentLeaderIndex = ((int)round % spec.EpochLength % masternodes.Length);
        return masternodes[currentLeaderIndex];
    }
}

internal class CandidateContainer(List<PrivateKey> candidates)
{
    public List<PrivateKey> MasternodeCandidates { get; set; } = candidates;
}
