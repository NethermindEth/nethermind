// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Api.Extensions;

/// <summary>
/// Assemblies containing instances of this interface will be the ones
/// used to load custom initialization steps.
/// </summary>
public interface IInitializationPlugin : INethermindPlugin
{
}
