// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class ModExpPrecompilePreEip2565
{
    /// <inheritdoc/>
    /// <remarks>
    /// EIP-2565 has been active since Berlin and the guest is built for later forks, so this cannot
    /// be reached. It throws rather than returning a value: a precompile that fabricates a result
    /// would put a wrong state root into a proof if the assumption ever stopped holding.
    /// </remarks>
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        throw new NotSupportedException();
}
