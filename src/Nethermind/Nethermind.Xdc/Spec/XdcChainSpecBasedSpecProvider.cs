// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using System;
using System.Collections.Generic;

namespace Nethermind.Xdc.Spec;

public class XdcChainSpecBasedSpecProvider(ChainSpec chainSpec,
    XdcChainSpecEngineParameters chainSpecEngineParameters,
    ILogManager logManager)
    : ChainSpecBasedSpecProvider(chainSpec, logManager)
{
    private const int ExtraVanity = 32; // Fixed number of extra-data prefix bytes reserved for signer vanity
    private const int ExtraSeal = 65; // Fixed number of extra-data suffix bytes reserved for signer seal

    protected override ReleaseSpec CreateEmptyReleaseSpec() => new XdcReleaseSpec();
    protected override ReleaseSpec CreateReleaseSpec(ChainSpec chainSpec, long releaseStartBlock, ulong? releaseStartTimestamp = null)
    {
        var releaseSpec = (XdcReleaseSpec)base.CreateReleaseSpec(chainSpec, releaseStartBlock, releaseStartTimestamp);

        releaseSpec.EpochLength = chainSpecEngineParameters.Epoch;
        releaseSpec.Gap = chainSpecEngineParameters.Gap;
        releaseSpec.SwitchEpoch = chainSpecEngineParameters.SwitchEpoch;
        releaseSpec.SwitchBlock = chainSpecEngineParameters.SwitchBlock;
        releaseSpec.V2Configs = chainSpecEngineParameters.V2Configs;
        releaseSpec.FoundationWallet = chainSpecEngineParameters.FoundationWalletAddr;
        releaseSpec.Reward = chainSpecEngineParameters.Reward;
        releaseSpec.MasternodeVotingContract = chainSpecEngineParameters.MasternodeVotingContract;
        releaseSpec.BlockSignerContract = chainSpecEngineParameters.BlockSignerContract;

        releaseSpec.IsTipTrc21FeeEnabled = (chainSpecEngineParameters.TipTrc21Fee ?? 0) <= releaseStartBlock;
        releaseSpec.IsBlackListingEnabled = chainSpecEngineParameters.BlackListHFNumber <= releaseStartBlock;
        releaseSpec.IsTIP2019 = chainSpecEngineParameters.TIP2019Block <= releaseStartBlock;
        releaseSpec.IsTIPXDCXMiner = chainSpecEngineParameters.TipXDCX <= releaseStartBlock && releaseStartBlock < chainSpecEngineParameters.TIPXDCXMinerDisable;
        releaseSpec.IsDynamicGasLimitBlock = chainSpecEngineParameters.DynamicGasLimitBlock <= releaseStartBlock;

        releaseSpec.MergeSignRange = chainSpecEngineParameters.MergeSignRange;
        releaseSpec.BlackListedAddresses = new(chainSpecEngineParameters.BlackListedAddresses ?? []);

        releaseSpec.RandomizeSMCBinary = chainSpecEngineParameters.RandomizeSMCBinary;

        releaseSpec.XDCXLendingFinalizedTradeAddressBinary = chainSpecEngineParameters.XDCXLendingFinalizedTradeAddressBinary;
        releaseSpec.XDCXLendingAddressBinary = chainSpecEngineParameters.XDCXLendingAddressBinary;
        releaseSpec.XDCXAddressBinary = chainSpecEngineParameters.XDCXAddressBinary;
        releaseSpec.TradingStateAddressBinary = chainSpecEngineParameters.TradingStateAddressBinary;

        releaseSpec.LimitPenaltyEpoch = chainSpecEngineParameters.LimitPenaltyEpoch;
        releaseSpec.LimitPenaltyEpochV2 = chainSpecEngineParameters.LimitPenaltyEpochV2;
        releaseSpec.RangeReturnSigner = chainSpecEngineParameters.RangeReturnSigner;

        releaseSpec.RangeReturnSigner = 150;
        releaseSpec.TipUpgradePenalty = long.MaxValue;

        
        releaseSpec.ApplyV2Config(0);

        if (releaseSpec.SwitchBlock == 0)
        {
            //We can parse genesis masternodes from genesis if the chain starts as V2
            releaseSpec.GenesisMasterNodes = ParseGenesisMasternodes(chainSpec);
        }
        else
        {
            releaseSpec.GenesisMasterNodes = chainSpecEngineParameters.GenesisMasternodes;
        }

        return releaseSpec;
    }

    private Address[] ParseGenesisMasternodes(ChainSpec chainSpec)
    {
        int length = (chainSpec.Genesis.ExtraData.Length - ExtraVanity - ExtraSeal) / Address.Size;
        Address[] signers = new Address[length];
        for (int i = 0; i < length; i++)
        {
            signers[i] = new Address(chainSpec.Genesis.ExtraData.AsSpan(ExtraVanity + i * Address.Size, Address.Size));
        }
        return signers;
    }

}
