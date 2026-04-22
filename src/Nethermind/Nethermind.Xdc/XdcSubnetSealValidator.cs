// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;

namespace Nethermind.Xdc;

/// <summary>
/// Seal validation for XDC subnet: epoch switch follows block-number modulo epoch (xdc-subnet), and gap+1 blocks may carry penalties.
/// </summary>
internal sealed class XdcSubnetSealValidator(
    ISubnetMasternodesCalculator masternodesCalculator,
    IEpochSwitchManager epochSwitchManager,
    ISpecProvider specProvider) : XdcSealValidator(masternodesCalculator, epochSwitchManager, specProvider)
{
    private readonly EthereumEcdsa _ethereumEcdsa = new(0);
    private readonly XdcHeaderDecoder _headerDecoder = new();

    public override bool ValidateParams(BlockHeader parent, BlockHeader header, out string error)
    {
        if (header is not XdcSubnetBlockHeader xdcHeader)
            throw new ArgumentException($"Only type of {nameof(XdcSubnetBlockHeader)} is allowed, but got type {header.GetType().Name}.", nameof(header));

        if (!base.ValidateParams(parent, header, out error))
        {
            return false;
        }
        IXdcReleaseSpec spec = SpecProvider.GetXdcSpec(xdcHeader);
        if (XdcSubnetConsensusRules.IsGapPlusOneBlock(xdcHeader.Number, spec.EpochLength, spec.Gap))
        {
            (Address[] masternodes, Address[] penaltiesAddresses) = masternodesCalculator.GetNextEpochCandidatesAndPenalties(xdcHeader.ParentHash);

            if (!xdcHeader.NextValidatorsAddress.SequenceEqual(masternodes))
            {
                error = "NextValidators do not match snapshot next epoch candidates.";
                return false;
            }

            if (!xdcHeader.PenaltiesAddress.SequenceEqual(penaltiesAddresses))
            {
                error = "Penalties do not match snapshot next epoch penalties.";
                return false;
            }
        }
        else
        {
            if (xdcHeader.NextValidators is not null && xdcHeader.NextValidators.Length != 0)
            {
                error = "NextValidators must be empty outside gap+1 blocks.";
                return false;
            }

            if (xdcHeader.Penalties is not null && xdcHeader.Penalties.Length != 0)
            {
                error = "Penalties must be empty outside gap+1 blocks.";
                return false;
            }
        }

        error = null;
        return true;
    }

    protected override bool ValidateNonEpochFields(XdcBlockHeader xdcHeader, out string? error)
    {
        if (xdcHeader.Validators is not null && xdcHeader.Validators.Length != 0)
        {
            error = "Validators are not empty in non-epoch switch header.";
            return false;
        }
        error = null;
        return true;
    }
}
