// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        releaseSpec.BlockSignerContract = chainSpecEngineParameters.BlockSignerContract;

        releaseSpec.IsBlackListingEnabled = chainSpecEngineParameters.BlackListHFNumber <= releaseStartBlock;
        releaseSpec.IsTIP2019 = chainSpecEngineParameters.TIP2019Block <= releaseStartBlock;
        releaseSpec.IsTIPXDCXMiner = chainSpecEngineParameters.TipXDCX <= releaseStartBlock && releaseStartBlock < chainSpecEngineParameters.TIPXDCXMinerDisable;

        releaseSpec.MergeSignRange = chainSpecEngineParameters.MergeSignRange;
        releaseSpec.BlackListedAddresses = chainSpecEngineParameters.BlackListedAddresses;

        releaseSpec.RandomizeSMCBinary = chainSpecEngineParameters.RandomizeSMCBinary;

        releaseSpec.XDCXLendingFinalizedTradeAddressBinary = chainSpecEngineParameters.XDCXLendingFinalizedTradeAddressBinary;
        releaseSpec.XDCXLendingAddressBinary = chainSpecEngineParameters.XDCXLendingAddressBinary;
        releaseSpec.XDCXAddressBinary = chainSpecEngineParameters.XDCXAddressBinary;
        releaseSpec.TradingStateAddressBinary = chainSpecEngineParameters.TradingStateAddressBinary;

        releaseSpec.ApplyV2Config(0);

        return releaseSpec;
    }

}
