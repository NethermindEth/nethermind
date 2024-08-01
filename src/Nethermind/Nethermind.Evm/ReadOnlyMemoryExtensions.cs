// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm
{
    public static class ReadOnlyMemoryExtensions
    {
        public static bool StartsWith(this ReadOnlyMemory<byte> inputData, byte startingByte)
        {
            return inputData.Span[0] == startingByte;
        }
    }
}
