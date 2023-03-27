// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Api;

public interface IApiWithServiceDescriptors
{
    IServiceCollection ServiceDescriptors { get; }
}
