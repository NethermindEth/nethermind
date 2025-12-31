// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using System.Collections.Generic;

namespace Nethermind.Xdc.Spec;

public class XdcReleaseSpec : ReleaseSpec, IXdcReleaseSpec
{
    public int EpochLength { get; set; }
    public int Gap { get; set; }
    public long Reward { get; set; }
    public int SwitchEpoch { get; set; }
    public long SwitchBlock { get; set; }
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
    public List<V2ConfigParams> V2Configs { get; set; } = new List<V2ConfigParams>();

    public Address[] GenesisMasterNodes { get; set; }
    public long BlackListHFNumber { get; set; }
    public long EpochBlockOpening { get; set; }
    public long EpochBlockRandomize { get; set; }
    public long MergeSignRange { get; set; }
    public long TIP2019Block { get; set; }
    public Address[] BlackListedAddresses { get; set; }
    public long EpochBlockSecret { get; set; }
    public Address BlockSignerContract { get; set; }
    public Address RandomizeSMCBinary { get; set; }
    public Address XDCXLendingFinalizedTradeAddressBinary { get; set; }
    public Address XDCXLendingAddressBinary { get; set; }
    public Address XDCXAddressBinary { get; set; }
    public Address TradingStateAddressBinary { get; set; }
    public Address FoundationWallet { get; set; }
    public Address MasternodeVotingContract { get; set; }

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

    public static XdcReleaseSpec FromReleaseSpec(IReleaseSpec spec)
    {
        var xdcSpec = new XdcReleaseSpec();

        var baseType = typeof(ReleaseSpec);
        var properties = baseType.GetProperties();
        foreach (var property in properties)
        {
            if (property.CanRead && property.CanWrite)
            {
                var value = property.GetValue(spec);
                property.SetValue(xdcSpec, value);
            }
        }

        return xdcSpec;
    }
}

public interface IXdcReleaseSpec : IReleaseSpec
{
    public int EpochLength { get; }
    public int Gap { get; }
    public long Reward { get; }
    public int SwitchEpoch { get; set; }
    public long SwitchBlock { get; set; }
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
    Address[] GenesisMasterNodes { get; set; }
    long BlackListHFNumber { get; set; }
    long EpochBlockOpening { get; set; }
    long EpochBlockRandomize { get; set; }
    long MergeSignRange { get; set; }
    long TIP2019Block { get; set; }

    public Address BlockSignerContract { get; set; }
    public Address RandomizeSMCBinary { get; set; }
    public Address XDCXLendingFinalizedTradeAddressBinary { get; set; }
    public Address XDCXLendingAddressBinary { get; set; }
    public Address XDCXAddressBinary { get; set; }
    public Address TradingStateAddressBinary { get; set; }

    Address[] BlackListedAddresses { get; set; }
    long EpochBlockSecret { get; set; }

    Address FoundationWallet { get; set; }
    Address MasternodeVotingContract { get; set; }

    public void ApplyV2Config(ulong round);
}
