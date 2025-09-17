// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Spec;

public class XdcSpecProvider(ISpecProvider baseSpecProvider) : SpecProviderDecorator(baseSpecProvider)
{
    public override IReleaseSpec GetSpecInternal(ForkActivation forkActivation)
    {
        return new XdcReleaseSpec(base.GetSpecInternal(forkActivation));
    }
}

public interface IXdcReleaseSpec : IReleaseSpec
{
    public long Gap { get; }
    public int EpochLength { get; }
    public int SwitchEpoch { get; set; }
    public UInt256 SwitchBlock { get; set; }
    public int MaxMasternodes { get; set; }          // v2 max masternodes
    public int MaxProtectorNodes { get; set; }       // v2 max ProtectorNodes
    public int MaxObserverNodes { get; set; }        // v2 max ObserverNodes
    public ulong SwitchRound { get; set; }           // v1 to v2 switch block number
    public int MinePeriod { get; set; }              // Miner mine period to mine a block
    public int TimeoutSyncThreshold { get; set; }    // send syncInfo after number of timeout
    public int TimeoutPeriod { get; set; }           // Duration in ms
    public double CertThreshold { get; set; }        // Necessary number of messages from master nodes to form a certificate
    public double MasternodeReward { get; set; }     // Block reward per master node (core validator) - unit Ether
    public double ProtectorReward { get; set; }      // Block reward per protector - unit Ether
    public double ObserverReward { get; set; }       // Block reward per observer - unit Ether
    public int MinimumMinerBlockPerEpoch { get; set; }   // Minimum block per epoch for a miner to not be penalized
    public int LimitPenaltyEpoch { get; set; }           // Epochs in a row that a penalty node needs to be penalized
    public int MinimumSigningTx { get; set; }            // Signing txs that a node needs to produce to get out of penalty, after `LimitPenaltyEpoch`
    IXdcSubConfig[] Configs { get; set; }
}
