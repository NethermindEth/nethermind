// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
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
    public override bool ValidateParams(BlockHeader parent, BlockHeader header, out string error)
    {
        if (header is not XdcSubnetBlockHeader xdcHeader)
            throw new ArgumentException($"Only type of {nameof(XdcSubnetBlockHeader)} is allowed, but got type {header.GetType().Name}.", nameof(header));

        if (!base.ValidateParams(parent, header, out error))
        {
            return false;
        }
        IXdcReleaseSpec spec = SpecProvider.GetXdcSpec(xdcHeader);
        if (xdcHeader.IsGapPlusOne(spec))
        {
            (Address[] masternodes, Address[] penaltiesAddresses) = masternodesCalculator.GetNextEpochCandidatesAndPenalties(xdcHeader.ParentHash);

            if (xdcHeader.NextValidatorsAddress is null || !xdcHeader.NextValidatorsAddress.ListsAreEqual(masternodes))
            {
                error = "NextValidators do not match snapshot next epoch candidates.";
                return false;
            }

            if (xdcHeader.PenaltiesAddress is null || !xdcHeader.PenaltiesAddress.ListsAreEqual(penaltiesAddresses))
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

    protected override bool ValidateEpochFields(XdcBlockHeader xdcHeader, Address[] masternodes, Address[] penalties, out string? error)
    {
        if (xdcHeader.Validators is null || xdcHeader.Validators.Length == 0)
        {
            error = "Empty validators list on epoch switch block.";
            return false;
        }
        if (xdcHeader.Validators.Length % Address.Size != 0)
        {
            error = "Invalid signer list on checkpoint block.";
            return false;
        }
        if (!xdcHeader.ValidatorsAddress.SequenceEqual(masternodes))
        {
            error = "Validators does not match what's stored in snapshot minus its penalty.";
            return false;
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
