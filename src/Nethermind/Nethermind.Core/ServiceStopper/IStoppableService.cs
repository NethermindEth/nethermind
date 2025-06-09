// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Core.ServiceStopper;

/// <summary>
/// Declare a component as stoppable that will be stopped on app shutdown. Unlike <see cref="IAsyncDisposable"/>,
/// all <see cref="IStoppableService.StopAsync"/> is called in parallel before any disposal. So the ordering
/// is not guaranteed.
/// </summary>
public interface IStoppableService
{
    Task StopAsync();
    string Description => GetType().Name;
}
