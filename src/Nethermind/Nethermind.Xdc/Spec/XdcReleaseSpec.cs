// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Nethermind.Xdc.Spec;

public class XdcReleaseSpec : ReleaseSpec, IXdcReleaseSpec
{
    public ulong EpochLength { get; set; }
    public ulong Gap { get; set; }
    public ulong Reward { get; set; }
    public ulong SwitchEpoch { get; set; }
    public ulong SwitchBlock { get; set; }
    public int MaxMasternodes { get; set; }              // v2 max masternodes
    public int MaxProtectorNodes { get; set; }           // v2 max ProtectorNodes
    public int MaxObserverNodes { get; set; }            // v2 max ObserverNodes
    public ulong SwitchRound { get; set; }               // v1 to v2 switch block number
    public ulong MinePeriod { get; set; }                  // Miner mine period to mine a block
    public int TimeoutSyncThreshold { get; set; }        // send syncInfo after number of timeout
    public int TimeoutPeriod { get; set; }               // Duration in ms
    public double CertificateThreshold { get; set; }     // Necessary number of messages from master nodes to form a certificate
    public UInt256 MasternodeReward { get; set; }        // Block reward per masternode (core validator) in Wei
    public UInt256 ProtectorReward { get; set; }         // Block reward per protector in Wei
    public UInt256 ObserverReward { get; set; }          // Block reward per observer in Wei
    public int MinimumMinerBlockPerEpoch { get; set; }   // Minimum block per epoch for a miner to not be penalized
    public ulong LimitPenaltyEpoch { get; set; }         // Epochs in a row that a penalty node needs to be penalized
    public ulong LimitPenaltyEpochV2 { get; set; }       // Epochs in a row that a penalty node needs to be penalized
    public int MinimumSigningTx { get; set; }            // Signing txs that a node needs to produce to get out of penalty, after `LimitPenaltyEpoch`
    public List<V2ConfigParams> V2Configs { get; set; } = [];

    public Address[] GenesisMasterNodes { get; set; }
    public ulong MergeSignRange { get; set; }
    public HashSet<Address> BlackListedAddresses { get; set; }
    public Address BlockSignerContract { get; set; }
    public Address RandomizeSMCBinary { get; set; }
    public Address XDCXLendingFinalizedTradeAddressBinary { get; set; }
    public Address XDCXLendingAddressBinary { get; set; }
    public Address XDCXAddressBinary { get; set; }
    public Address TradingStateAddressBinary { get; set; }
    public ulong TIP2019Block { get; set; }
    public Address FoundationWallet { get; set; }
    public Address MasternodeVotingContract { get; set; }
    public bool IsTipUpgradeRewardEnabled { get; set; }
    public bool IsTipUpgradePenaltyEnabled { get; set; }
    public bool IsTipTrc21FeeEnabled { get; set; }
    public bool IsBlackListingEnabled { get; set; }
    public bool IsTIP2019 { get; set; }
    public bool IsTIPXDCXMiner { get; set; }
    public bool IsDynamicGasLimitBlock { get; set; }
    public ulong RangeReturnSigner { get; set; }

    public void ApplyV2Config(ulong round)
    {
        V2ConfigParams configParams = GetConfigAtRound(V2Configs, round);
        SwitchRound = configParams.SwitchRound;
        MaxMasternodes = configParams.MaxMasternodes;
        CertificateThreshold = configParams.CertificateThreshold;
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
        XdcReleaseSpec xdcSpec = new();

        Type baseType = typeof(ReleaseSpec);
        PropertyInfo[] properties = baseType.GetProperties();
        foreach (PropertyInfo property in properties)
        {
            if (property.CanRead && property.CanWrite)
            {
                object value = property.GetValue(spec);
                property.SetValue(xdcSpec, value);
            }
        }

        return xdcSpec;
    }
}

public interface IXdcReleaseSpec : IReleaseSpec
{
    public ulong EpochLength { get; }
    public ulong Gap { get; }
    public ulong Reward { get; }
    public ulong SwitchEpoch { get; set; }
    public ulong SwitchBlock { get; set; }
    public int MaxMasternodes { get; set; }              // v2 max masternodes
    public int MaxProtectorNodes { get; set; }           // v2 max ProtectorNodes
    public int MaxObserverNodes { get; set; }            // v2 max ObserverNodes
    public ulong SwitchRound { get; set; }               // v1 to v2 switch block number
    public ulong MinePeriod { get; set; }                  // Miner mine period to mine a block
    public int TimeoutSyncThreshold { get; set; }        // send syncInfo after number of timeout
    public int TimeoutPeriod { get; set; }               // Duration in ms
    public double CertificateThreshold { get; set; }     // Necessary number of messages from master nodes to form a certificate
    public UInt256 MasternodeReward { get; set; }        // Block reward per masternode (core validator) in Wei
    public UInt256 ProtectorReward { get; set; }         // Block reward per protector in Wei
    public UInt256 ObserverReward { get; set; }          // Block reward per observer in Wei
    public int MinimumMinerBlockPerEpoch { get; set; }   // Minimum block per epoch for a miner to not be penalized
    public ulong LimitPenaltyEpoch { get; set; }         // Epochs in a row that a penalty node needs to be penalized
    public ulong LimitPenaltyEpochV2 { get; set; }       // Epochs in a row that a penalty node needs to be penalized
    public ulong RangeReturnSigner { get; set; }
    public int MinimumSigningTx { get; set; }            // Signing txs that a node needs to produce to get out of penalty, after `LimitPenaltyEpoch`
    public List<V2ConfigParams> V2Configs { get; set; }
    public Address[] GenesisMasterNodes { get; set; }
    public ulong MergeSignRange { get; set; }
    public Address BlockSignerContract { get; set; }
    public Address RandomizeSMCBinary { get; set; }
    public Address XDCXLendingFinalizedTradeAddressBinary { get; set; }
    public Address XDCXLendingAddressBinary { get; set; }
    public Address XDCXAddressBinary { get; set; }
    public Address TradingStateAddressBinary { get; set; }
    public HashSet<Address> BlackListedAddresses { get; set; }
    public Address FoundationWallet { get; set; }
    public Address MasternodeVotingContract { get; set; }
    public bool IsTipUpgradeRewardEnabled { get; set; }
    public bool IsTipTrc21FeeEnabled { get; set; }
    public bool IsBlackListingEnabled { get; set; }
    public bool IsTIP2019 { get; set; }
    public bool IsTIPXDCXMiner { get; set; }
    public bool IsTipUpgradePenaltyEnabled { get; set; }
    public bool IsDynamicGasLimitBlock { get; set; }
    public ulong TIP2019Block { get; set; }
    public void ApplyV2Config(ulong round);
}
