// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using System.Collections.Generic;

namespace Nethermind.Xdc.Spec;
public class XdcReleaseSpec : ReleaseSpec, IXdcReleaseSpec
{
    public int EpochLength { get; set; }
    public long Gap { get; set; }
    public int SwitchEpoch { get; set; }
    public UInt256 SwitchBlock { get; set; }
    public int MaxMasternodes { get; set; }              // v2 max masternodes
    public int MaxProtectorNodes { get; set; }           // v2 max ProtectorNodes
    public int MaxObserverNodes { get; set; }            // v2 max ObserverNodes
    public ulong SwitchRound { get; set; }               // v1 to v2 switch block number
    public int MinePeriod { get; set; }                  // Miner mine period to mine a block
    public int TimeoutSyncThreshold { get; set; }        // send syncInfo after number of timeout
    public int TimeoutPeriod { get; set; }               // Duration in ms
    public double CertThreshold { get; set; }            // Necessary number of messages from master nodes to form a certificate
    public double MasternodeReward { get; set; }         // Block reward per master node (core validator) - unit Ether
    public double ProtectorReward { get; set; }          // Block reward per protector - unit Ether
    public double ObserverReward { get; set; }           // Block reward per observer - unit Ether
    public int MinimumMinerBlockPerEpoch { get; set; }   // Minimum block per epoch for a miner to not be penalized
    public int LimitPenaltyEpoch { get; set; }           // Epochs in a row that a penalty node needs to be penalized
    public int MinimumSigningTx { get; set; }            // Signing txs that a node needs to produce to get out of penalty, after `LimitPenaltyEpoch`
    public List<V2ConfigParams> V2Configs { get; set; }


    public void ApplyV2Config(ulong round)
    {
        V2ConfigParams configParams = GetConfigAtRound(V2Configs, round);
        SwitchRound = configParams.SwitchRound;
        MaxMasternodes = configParams.MaxMasternodes;
        CertThreshold = configParams.CertThreshold;
        TimeoutSyncThreshold = configParams.TimeoutSyncThreshold;
        TimeoutPeriod = configParams.TimeoutPeriod;
        MinePeriod = configParams.MinePeriod;
    }

    internal static V2ConfigParams GetConfigAtRound(List<V2ConfigParams> list, ulong round)
    {
        // list.Count >= 1 and list[0].SwitchRound == 0 guaranteed by CheckConfig
        int lo = 0, hi = list.Count; // [lo,hi)
        while (lo + 1 < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (list[mid].SwitchRound <= round) lo = mid;
            else hi = mid;
        }
        return list[lo];
    }
}

public interface IXdcReleaseSpec : IReleaseSpec
{
    public int EpochLength { get; }
    public long Gap { get; }
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
    public List<V2ConfigParams> V2Configs { get; set; }
    public void ApplyV2Config(ulong round);
}
