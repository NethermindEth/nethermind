// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Api.Extensions;

/// <summary>
/// Allows plugins to register ASP.NET Core middleware without the Runner
/// needing to reference the plugin assembly directly.
/// </summary>
public interface IJsonRpcApplicationConfigurer
{
    void Configure(object applicationBuilder);
}
