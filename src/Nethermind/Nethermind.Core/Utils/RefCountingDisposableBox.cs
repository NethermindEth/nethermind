// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Utils;

public class RefCountingDisposableBox<T>(T item): RefCountingDisposable
    where T : IDisposable
{

    public T Item => item;

    protected override void CleanUp()
    {
        Item.Dispose();
    }
}
