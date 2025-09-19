// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using System;

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

        return base.Validate(header, parent, isUncle, out error);
    }



}
