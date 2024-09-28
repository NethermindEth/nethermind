// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;

namespace Nethermind.Api.Extensions
{
    public interface ISynchronizationPlugin : INethermindPlugin
    {
        void ConfigureSynchronizationBuilder(ContainerBuilder containerBuilder);
        Task InitSynchronization(IContainer container);
    }
}
