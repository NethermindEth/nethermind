// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Xdc.Spec;

public class XdcChainSpecBasedSpecProvider(ChainSpec chainSpec,
    XdcChainSpecEngineParameters chainSpecEngineParameters,
    ILogManager logManager)
    : ChainSpecBasedSpecProvider(chainSpec, logManager)
{
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
        releaseSpec.RelayerRegistrationSMC = chainSpecEngineParameters.RelayerRegistrationSMC;
        releaseSpec.TRC21IssuerSMC = chainSpecEngineParameters.TRC21IssuerSMC;
        releaseSpec.BlockSignerContract = chainSpecEngineParameters.BlockSignerContract;

        releaseSpec.IsTipTrc21FeeEnabled = (chainSpecEngineParameters.TipTrc21Fee ?? 0) <= releaseStartBlock;
        releaseSpec.IsBlackListingEnabled = chainSpecEngineParameters.BlackListHFNumber <= releaseStartBlock;
        releaseSpec.IsTIP2019 = chainSpecEngineParameters.TIP2019Block <= releaseStartBlock;
        releaseSpec.IsTIPXDCXMiner = chainSpecEngineParameters.TipXDCX <= releaseStartBlock && releaseStartBlock < chainSpecEngineParameters.TIPXDCXMinerDisable;
        releaseSpec.IsDynamicGasLimitBlock = chainSpecEngineParameters.DynamicGasLimitBlock <= releaseStartBlock;
        releaseSpec.IsTipUpgradePenaltyEnabled = (chainSpecEngineParameters.TipUpgradePenalty ?? long.MaxValue) <= releaseStartBlock;

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

        releaseSpec.ApplyV2Config(0);

        if (releaseSpec.SwitchBlock == 0)
        {
            //We can parse genesis masternodes from genesis if the chain starts as V2
            byte[] genesisExtraData = chainSpec.Genesis?.ExtraData
                ?? throw new ArgumentException("Genesis ExtraData is required when SwitchBlock is 0", nameof(chainSpec));
            releaseSpec.GenesisMasterNodes = genesisExtraData.ParseV1Masternodes();
        }
        else
        {
            releaseSpec.GenesisMasterNodes = chainSpecEngineParameters.GenesisMasternodes;
        }

        return releaseSpec;
    }

}
