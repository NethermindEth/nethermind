// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.AsyncProcessor;

public interface IAsyncProcessor
{
    Task Process<T>(IAsyncEnumerable<T> source, Func<T, Task> process, CancellationToken token = default);
}
