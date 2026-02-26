// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Threading;

public class MockLock
{
    public Disposable Acquire() => new();

    public readonly ref struct Disposable
    {
        public void Dispose() { }
    }
}
