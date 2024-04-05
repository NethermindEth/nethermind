// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Resettables;

public class DisposableResettableList<T> : ResettableList<T>, IDisposable
{
    public void Dispose()
    {
        for (int index = 0; index < Count; index++)
        {
            this[index].TryDispose();
        }
    }
}
