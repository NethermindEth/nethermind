// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Api.Extensions;

public interface INethermindPlugin
{
    string Name { get; }

    string Description { get; }

    string Author { get; }

    /// <summary>
    /// Called during initialization to let plugins register custom transaction types and RLP decoders.
    /// Plugins should add decoders to the <paramref name="rlpBuilder"/> rather than mutating global state.
    /// </summary>
    void InitTxTypesAndRlpDecoders(INethermindApi api, RlpDecoderRegistryBuilder rlpBuilder) =>
        InitTxTypesAndRlpDecoders(api);

    void InitTxTypesAndRlpDecoders(INethermindApi api) { }

    Task Init(INethermindApi nethermindApi) => Task.CompletedTask;

    Task InitNetworkProtocol() => Task.CompletedTask;

    Task InitRpcModules() => Task.CompletedTask;
    bool MustInitialize => false;
    bool Enabled { get; }
    IModule? Module => null;
}
