// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;

namespace Nethermind.Core.Container;

/// <summary>
/// Marker interface for module that configure main block processor and RPC that assume same configuration as
/// main block processor. This is not applied in block producer. Plugins can add module for some weird case.
/// </summary>
public interface IBlockValidationModule : IModule
{
}
