// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace Nethermind.Api.Extensions;

public interface IServiceDescriptorsPlugin
{
    Task InitServiceDescriptors(IServiceCollection services);
}
