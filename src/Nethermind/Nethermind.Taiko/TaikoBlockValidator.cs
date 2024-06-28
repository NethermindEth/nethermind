// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Taiko;

public class TaikoBlockValidator(
    ITxValidator? txValidator,
    IHeaderValidator? headerValidator,
    IUnclesValidator? unclesValidator,
    ISpecProvider? specProvider,
    ILogManager? logManager) : BlockValidator(txValidator, headerValidator, unclesValidator, specProvider, logManager)
{
    protected override bool ValidateEip4844Fields(Block block, IReleaseSpec spec, out string? error)
    {
        // for some reason they don't validate these fields in taiko-geth
        error = null;
        return true;
    }

}
