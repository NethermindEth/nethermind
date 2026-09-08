// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        public T Config<T>() where T : IConfig => ConfigProvider.GetConfig<T>();

        (IApiWithNetwork GetFromApi, INethermindApi SetInApi) ForRpc => (this, this);
    }

    public static class NethermindApiExtensions
    {
        /// <summary>
        /// Registers the RPC decoder and transaction validator for a transaction type.
        /// </summary>
        /// <remarks>
        /// A per-type validator may cover fewer rules than the default full validator. Chain plugins that register
        /// one must ensure their <see cref="ISpecChangeTxValidator"/> validates any omitted fork-sensitive rules in
        /// <see cref="ISpecChangeTxValidator.IsWellFormedAfterFullValidation"/>.
        /// </remarks>
        /// <typeparam name="T">The RPC transaction representation to register.</typeparam>
        /// <param name="api">The Nethermind API receiving the registrations.</param>
        /// <param name="decoder">The decoder for the transaction type.</param>
        /// <param name="validator">The full validator for the transaction type.</param>
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
