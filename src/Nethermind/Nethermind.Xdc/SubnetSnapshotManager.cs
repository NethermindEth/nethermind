// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal sealed class SubnetSnapshotManager(
    [KeyFilter(XdcRocksDbConfigFactory.XdcSnapshotDbName)] IDb snapshotDb,
    IBlockTree blockTree,
    IMasternodeVotingContract votingContract,
    ISpecProvider specProvider,
    IStateReader stateReader,
    ILogManager logManager,
    IPenaltyHandler penaltyHandler)
    : BaseSnapshotManager<SubnetSnapshot>(
        snapshotDb,
        blockTree,
        votingContract,
        specProvider,
        stateReader,
        logManager,
        new SubnetSnapshotDecoder(),
        cacheName: "XDC Subnet Snapshot cache"), ISubnetSnapshotManager
{
    public override Snapshot CreateInitialSnapshot(ulong number, Hash256 hash, Address[] genesisMasterNodes) =>
        new SubnetSnapshot(number, hash, genesisMasterNodes);

    protected override SubnetSnapshot CreateSnapshot(XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        Address[] candidates = header.IsGenesis ? spec.GenesisMasterNodes : VotingContract.GetCandidatesByStake(header);
        Address[] penalties = header.IsGenesis ? [] : penaltyHandler.HandlePenalties(header.Number, header.ParentHash!, candidates);

        return new SubnetSnapshot(header.Number, header.Hash!, candidates, penalties);
    }

    public SubnetSnapshot GetSnapshotByHash(Hash256 headerHash) => GetSnapshot(headerHash) ?? throw new ArgumentException($"No snapshot found for header hash {headerHash}");
}
