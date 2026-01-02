// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.AsyncProcessor;

public sealed class SequentialProcessor : IAsyncProcessor
{
    public async Task Process<T>(IAsyncEnumerable<T> source, Func<T, Task> process, CancellationToken token = default)
    {
        await foreach (var t in source)
        {
            await process(t);
        }
    }
}
