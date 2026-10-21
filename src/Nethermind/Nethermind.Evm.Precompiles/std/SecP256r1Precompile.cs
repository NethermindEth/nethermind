// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles;

public partial class SecP256r1Precompile
{
    public static Address Address { get; } = Address.FromNumber(0x100);

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        Metrics.SecP256r1Precompile++;

        return SecP256r1.VerifySignature(inputData) ? _successResult : [];
    }
}
