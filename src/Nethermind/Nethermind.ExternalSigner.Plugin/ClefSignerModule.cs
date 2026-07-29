// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Configuration;
using Autofac;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.JsonRpc.Client;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Wallet;

namespace Nethermind.ExternalSigner.Plugin;

/// <summary>
/// Registers the remote Clef signer as the node's <see cref="IWallet"/> and, when mining, its block-author
/// <see cref="ISigner"/>/<see cref="ISignerStore"/>, overriding the local defaults.
/// </summary>
/// <remarks>
/// Only loaded when <c>Mining.Signer</c> is set (the plugin is otherwise disabled), so the wallet registration is
/// unconditional here. The signer is registered only when mining is enabled, matching the previous behaviour where an
/// external <c>EngineSigner</c> was wired up solely for block production.
/// </remarks>
public class ClefSignerModule(IMiningConfig miningConfig) : Module
{
    private const int RemoteSignerTimeoutSeconds = 10;

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        if (!Uri.TryCreate(miningConfig.Signer, UriKind.Absolute, out Uri? uri))
        {
            throw new ConfigurationErrorsException($"{miningConfig.Signer} must be a valid uri.");
        }

        builder
            .AddSingleton<BasicJsonRpcClient, ILogManager>(logManager => CreateRpcClient(uri, logManager))
            .AddSingleton<ClefWallet, BasicJsonRpcClient>(rpcClient => new ClefWallet(rpcClient))
            .Bind<IWallet, ClefWallet>();

        if (miningConfig.Enabled)
        {
            builder
                .AddSingleton<ISigner, ClefWallet, IKeyStoreConfig>(CreateSigner)
                .AddSingleton<ISignerStore>(static ctx => (ISignerStore)ctx.Resolve<ISigner>());
        }
    }

    private static BasicJsonRpcClient CreateRpcClient(Uri uri, ILogManager logManager) =>
        new(uri, new EthereumJsonSerializer([new ChecksumAddressConverter()]), logManager, TimeSpan.FromSeconds(RemoteSignerTimeoutSeconds));

    private static ISigner CreateSigner(ClefWallet clefWallet, IKeyStoreConfig keyStoreConfig)
    {
        try
        {
            Address? blockAuthorAccount = string.IsNullOrEmpty(keyStoreConfig.BlockAuthorAccount) ? null : new Address(keyStoreConfig.BlockAuthorAccount);
            return ClefSigner.Create(clefWallet, blockAuthorAccount);
        }
        catch (HttpRequestException e)
        {
            throw new NetworkingException("Remote signer did not respond during setup.", NetworkExceptionType.TargetUnreachable, e);
        }
    }
}
