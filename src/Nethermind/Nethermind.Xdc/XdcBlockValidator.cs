// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

using Nethermind.Core.Crypto;

namespace Nethermind.Xdc;

public class XdcBlockValidator(
    ITxValidator txValidator,
    IHeaderValidator headerValidator,
    IUnclesValidator unclesValidator,
    ISpecProvider specProvider,
    ILogManager logManager, ILeanProofVerifier leanProofVerifier) : BlockValidator(txValidator, headerValidator, unclesValidator, specProvider, logManager, leanProofVerifier)
{
    protected override bool ValidateEip4844Fields(Block block, IReleaseSpec spec, ref string? error) => true;
}
