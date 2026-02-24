// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Stateless.Execution;
using System.Buffers.Binary;

namespace Nethermind.Stateless.ZiskGuest;

class Program
{
    static int Main()
    {
        // TODO: Replace with Zisk.ReadInput() and parse the input into the expected format for StatelessExecutor
        Zisk.WriteLine("hella good");

        (int status, Hash256 hash) = StatelessExecutor.Execute();

        if (status == 0)
        {
            var size = sizeof(uint);

            for (int i = 0, count = hash.Bytes.Length / size; i < count; i++)
            {
                var start = i * size;

                Zisk.SetOutput(i, BinaryPrimitives.ReadUInt32BigEndian(hash.Bytes[start..(start + size)]));
            }
        }

        return status;
    }
}
