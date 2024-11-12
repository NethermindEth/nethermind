// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using Nethermind.Config;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
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
        public static void RegisterTxType<T>(this INethermindApi api, ITxDecoder decoder, ITxValidator validator) where T : TransactionForRpc, IFromTransaction<T>
        {
            ArgumentNullException.ThrowIfNull(api.TxValidator);
            if (decoder.Type != T.TxType) throw new ArgumentException($"TxType mismatch decoder: {decoder.Type}, RPC: {T.TxType}");

            api.TxValidator.RegisterValidator(T.TxType, validator);
            TxDecoder.Instance.RegisterDecoder(decoder);
            TransactionForRpc.RegisterTransactionType<T>();
        }
    }
}
