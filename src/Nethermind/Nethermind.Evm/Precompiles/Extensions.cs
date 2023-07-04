// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.Precompiles
{
    public static class Extensions
    {
        public static void PrepareEthInput(this ReadOnlyMemory<byte> inputData, Span<byte> inputDataSpan)
        {
            inputData.Span[..Math.Min(inputDataSpan.Length, inputData.Length)]
                .CopyTo(inputDataSpan[..Math.Min(inputDataSpan.Length, inputData.Length)]);
        }
    }
}
