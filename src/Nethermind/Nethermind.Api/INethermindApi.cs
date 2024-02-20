// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using Autofac;
using Nethermind.Config;

namespace Nethermind.Api
{
    public interface INethermindApi : IApiWithNetwork
    {
        public T Config<T>() where T : IConfig
        {
            return BaseContainer.Resolve<T>();
        }

        (IApiWithNetwork GetFromApi, INethermindApi SetInApi) ForRpc => (this, this);
    }
}
