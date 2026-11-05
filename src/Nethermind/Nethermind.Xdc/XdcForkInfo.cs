// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.Network;
using Nethermind.Synchronization;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

/// <summary>
/// EIP-2124 fork identity for XDC, whose fork schedule is only partly described by the spec transitions.
/// </summary>
/// <remarks>
/// The reference client gathers every fork-gating block number from its chain config (<c>ChainConfig.GatherForks</c>),
/// including XDC's own TIP blocks and the nested XDPoS 1.0 to 2.0 consensus switch. Most of those gate consensus or
/// fee rules rather than a release spec, so they never reach <see cref="ISpecProvider.TransitionActivations"/> and a
/// checksum built from the spec transitions alone is rejected by XDC peers.
/// <para>
/// XDC has no timestamp-based forks - the reference client's fork IDs are block numbers only - so the schedule is
/// merged on block number.
/// </para>
/// </remarks>
public class XdcForkInfo(
    ISpecProvider specProvider,
    ISyncServer syncServer,
    XdcChainSpecEngineParameters engineParameters) : ForkInfo(specProvider, syncServer)
{
    protected override ForkActivation[] GetForkActivations()
    {
        SortedSet<ForkActivation> activations = [.. base.GetForkActivations()];

        Add(engineParameters.SwitchBlock);
        Add(engineParameters.TIP2019Block);
        Add(engineParameters.TipSigningBlock);
        Add(engineParameters.TipRandomizeBlock);
        Add(engineParameters.TipIncreaseMasternodesBlock);
        Add(engineParameters.BlackListHFNumber);
        Add(engineParameters.TipNoHalvingMNRewardBlock);
        Add(engineParameters.TipXDCX);
        Add(engineParameters.TipXDCXLendingBlock);
        Add(engineParameters.TipXDCXCancellationFeeBlock);
        Add(engineParameters.TipTrc21Fee);
        Add(engineParameters.Gas50xBlock);
        Add(engineParameters.TIPXDCXMinerDisable);
        Add(engineParameters.TIPXDCXReceiverDisable);
        Add(engineParameters.DynamicGasLimitBlock);
        Add(engineParameters.TipUpgradeReward);
        Add(engineParameters.TipUpgradePenalty);

        return [.. activations];

        // A fork at genesis is part of the genesis ruleset, not a transition.
        void Add(ulong? block)
        {
            if (block is > 0)
            {
                activations.Add(new ForkActivation(block.Value));
            }
        }
    }
}
