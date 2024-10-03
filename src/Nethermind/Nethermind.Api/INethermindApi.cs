// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;
using Nethermind.TxPool;

namespace Nethermind.Api
{
    public interface INethermindApi : IApiWithNetwork
    {
        public T Config<T>() where T : IConfig
        {
            return ConfigProvider.GetConfig<T>();
        }

        (IApiWithNetwork GetFromApi, INethermindApi SetInApi) ForRpc => (this, this);
    }

    public static class NethermindApiExtensions
    {
        public static void RegisterTxType(this INethermindApi api, TxType type, ITxDecoder decoder, ITxValidator validator)
        {
            ArgumentNullException.ThrowIfNull(api.TxValidator);

            api.TxValidator.RegisterValidator(type, validator);
            TxDecoder.Instance.RegisterDecoder(decoder);
        }
    }
}
