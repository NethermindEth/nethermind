// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Api.Extensions;

/// <summary>
/// Assemblies containing instances of this interface will be the ones
/// used to load custom initialization steps.
/// </summary>
public interface IInitializationPlugin : INethermindPlugin
{
    /// <summary>
    /// This method will be called on the plugin instance
    /// decide whether or not we need to run initialization steps
    /// defined in its assembly. It receives the api to be able to
    /// look at the config.
    /// </summary>
    bool ShouldRunSteps(INethermindApi api);
}
