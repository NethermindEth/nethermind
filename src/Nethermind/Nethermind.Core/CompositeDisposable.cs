// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core;

public class CompositeDisposable : List<IDisposable>, IDisposable
{
    public void Dispose()
    {
        foreach (IDisposable disposable in this)
        {
            disposable.Dispose();
        }
    }
}
