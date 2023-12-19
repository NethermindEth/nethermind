// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Resettables;

public class DisposableResettableList<T> : ResettableList<T>, IDisposable
{
    public void Dispose()
    {
        for (int index = 0; index < Count; index++)
        {
            if (this[index] is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
