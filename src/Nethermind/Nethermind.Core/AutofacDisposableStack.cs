// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Core;

namespace Nethermind.Core;

/// <summary>
/// Hook up built in autofac's disposal stack to <see cref="IDisposableStack"/>
/// </summary>
/// <param name="lifetime"></param>
public class AutofacDisposableStack(ILifetimeScope lifetime) : IDisposableStack
{
    private readonly IDisposer _disposer = lifetime.Disposer;

    public void Push(IAsyncDisposable item)
    {
        _disposer.AddInstanceForAsyncDisposal(item);
    }

    public void Push(IDisposable item)
    {
        _disposer.AddInstanceForDisposal(item);
    }
}
