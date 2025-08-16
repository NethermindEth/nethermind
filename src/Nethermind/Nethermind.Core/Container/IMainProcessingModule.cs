// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;

namespace Nethermind.Core.Container;

/// <summary>
/// Marker interface for module that configure just the main block processor.
/// </summary>
public interface IMainProcessingModule : IModule
{
}
