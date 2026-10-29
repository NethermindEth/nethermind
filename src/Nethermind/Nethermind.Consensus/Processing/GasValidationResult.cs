// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Exceptions;

namespace Nethermind.Consensus.Processing;

public readonly record struct GasValidationResult(
    ulong BlockGasUsed,
    ulong BlockStateGasUsed,
    InvalidBlockException? Exception);
