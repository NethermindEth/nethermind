// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;



// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using System;
using System.Collections;

namespace Nethermind.Xdc;

public class XdcHeaderValidator : HeaderValidator
{
    public XdcHeaderValidator(IBlockTree? blockTree, ISealValidator? sealValidator, ISpecProvider? specProvider, ILogManager? logManager) : base(blockTree, sealValidator, specProvider, logManager)
    {
    }

    public override bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error)
    {
        if (header is not XdcBlockHeader xdcHeader)
            throw new ArgumentException($"Only type of {nameof(XdcBlockHeader)} is allowed, but got type {header.GetType().Name}.", nameof(header));

        if (xdcHeader.Validator == null || xdcHeader.Validator.Length == 0)
        {
            error = "Validator field is required in XDC header.";
            return false;
        }

        ExtraFieldsV2? extraFields = xdcHeader.DecodeQuorumCertificate();
        if (extraFields is null)
        {
            error = "ExtraData doesn't contain required fields.";
            return false;
        }

        //TODO verify QC

        if (xdcHeader.Nonce != XdcConstants.NonceDropVoteValue && header.Nonce != XdcConstants.NonceAuthVoteValue)
        {
            error = $"Invalid nonce value ({header.Nonce}) in XDC header.";
            return false;
        }

        if (xdcHeader.MixHash != Hash256.Zero)
        {
            error = $"Non-zero mix hash.";
            return false;
        }

        if (xdcHeader.UnclesHash != Keccak.OfAnEmptyString)
        {
            error = $"Cannot contain uncles.";
            return false;
        }

        if (!base.Validate(header, parent, isUncle, out error))
        {
            return false;
        }

        //IEnumerable<Address> masternodes;

        if (!xdcHeader.IsEpochSwitch(_specProvider))
        {
            if (xdcHeader.Validators?.Length != 0)
            {
                error = "Validators are not empty in non-epoch switch header.";
                return false;
            }
            if (xdcHeader.Penalties?.Length != 0)
            {
                error = "Penalties are not empty in non-epoch switch header.";
                return false;
            }
        }

        error =null;
        return true;
    }

    protected override bool ValidateExtraData(BlockHeader header, BlockHeader? parent, IReleaseSpec spec, bool isUncle, ref string? error)
    {
        return true;
    }

    protected override bool ValidateTotalDifficulty(BlockHeader parent, BlockHeader header, ref string? error)
    {
        if (header.Difficulty != 1)
        {
            error = "Total difficulty must be 1.";
            return false;
        }
        return true;
    }

    protected override bool ValidateTimestamp(BlockHeader header, BlockHeader parent, ref string? error)
    {
        //TODO fetch from spec
        const int SlotTime = 2; // seconds

        //TODO check if V2 header
        if (parent.Timestamp + SlotTime > header.Timestamp)
        {
            error = "Timestamp in header cannot be lower than ancestor plus slot time.";
            return false;
        }

        return true;
    }



}
