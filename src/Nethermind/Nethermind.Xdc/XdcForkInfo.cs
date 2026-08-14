// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.Network;
using Nethermind.Synchronization;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

/// <summary>
/// EIP-2124 fork identity for XDC, whose fork schedule includes the XDPoS 1.0 to 2.0 consensus switch.
/// </summary>
/// <remarks>
/// The switch block gates the consensus engine rather than a release spec, so it is absent from the spec
/// transitions every other chain derives its checksum from. The reference client folds it into the same
/// list (<c>ChainConfig.GatherForks</c>), and a checksum computed without it is rejected by every XDC peer.
/// </remarks>
public class XdcForkInfo(
    ISpecProvider specProvider,
    ISyncServer syncServer,
    XdcChainSpecEngineParameters engineParameters) : ForkInfo(specProvider, syncServer)
{
    protected override ForkActivation[] GetForkActivations()
    {
        ForkActivation[] transitions = base.GetForkActivations();
        ulong switchBlock = engineParameters.SwitchBlock;

        // A fork at genesis is part of the genesis ruleset, not a transition.
        if (switchBlock == 0)
            return transitions;

        ForkActivation switchActivation = new(switchBlock);
        List<ForkActivation> activations = new(transitions.Length + 1);
        activations.AddRange(transitions);
        if (activations.Contains(switchActivation))
            return transitions;

        activations.Add(switchActivation);
        activations.Sort();
        return [.. activations];
    }
}
