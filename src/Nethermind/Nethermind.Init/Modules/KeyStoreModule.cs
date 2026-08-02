// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Wallet;

namespace Nethermind.Init.Modules;

/// <summary>
/// Registers the file-backed <see cref="IKeyStore"/>, the local <see cref="IWallet"/>, and the default block-author
/// <see cref="ISigner"/>/<see cref="ISignerStore"/>.
/// </summary>
public class KeyStoreModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<ISymmetricEncrypter, AesEncrypter>()
            .AddSingleton<IKeyStoreIOSettingsProvider, PrivateKeyStoreIOSettingsProvider>()
            .AddSingleton<IKeyStore, FileKeyStore>()
            .AddKeyedSingleton<IPasswordProvider>(IPasswordProvider.ConfigOnly, static ctx => new KeyStorePasswordProvider(ctx.Resolve<IKeyStoreConfig>()))
            .AddKeyedSingleton<IPasswordProvider>(IPasswordProvider.ConsoleFallback, static ctx =>
            {
                IKeyStoreConfig keyStoreConfig = ctx.Resolve<IKeyStoreConfig>();
                return new KeyStorePasswordProvider(keyStoreConfig)
                    .OrReadFromConsole($"Provide password for validator account {keyStoreConfig.BlockAuthorAccount}");
            })
            .AddSingleton<IWallet>(CreateWallet)
            .AddSingleton<AccountUnlocker>()
            .AddSingleton<INodeKeyManager, NodeKeyManager>()
            .AddSingleton<ISigner>(CreateSigner)
            .AddSingleton<ISignerStore>(static ctx => (ISignerStore)ctx.Resolve<ISigner>());
    }

    private static IWallet CreateWallet(IComponentContext ctx)
    {
        ILogManager logManager = ctx.Resolve<ILogManager>();
        return ctx.Resolve<IInitConfig>() switch
        {
            { EnableUnsecuredDevWallet: true, KeepDevWalletInMemory: true } => new DevWallet(ctx.Resolve<IWalletConfig>(), logManager),
            { EnableUnsecuredDevWallet: true, KeepDevWalletInMemory: false } => new DevKeyStoreWallet(ctx.Resolve<IKeyStore>(), logManager),
            _ => CreateProtectedKeyStoreWallet(ctx, logManager),
        };
    }

    private static IWallet CreateProtectedKeyStoreWallet(IComponentContext ctx, ILogManager logManager)
    {
        IKeyStoreConfig keyStoreConfig = ctx.Resolve<IKeyStoreConfig>();
        ITimestamper timestamper = ctx.Resolve<ITimestamper>();
        ProtectedPrivateKeyFactory keyFactory = new(ctx.Resolve<ICryptoRandom>(), timestamper, keyStoreConfig.KeyStoreDirectory);
        return new ProtectedKeyStoreWallet(ctx.Resolve<IKeyStore>(), keyFactory, timestamper, logManager);
    }

    private static ISigner CreateSigner(IComponentContext ctx)
    {
        if (!ctx.Resolve<IMiningConfig>().Enabled) return NullSigner.Instance;

        return new Signer(
            ctx.Resolve<ISpecProvider>().ChainId,
            ctx.Resolve<INodeKeyManager>().LoadSignerKey(),
            ctx.Resolve<ILogManager>());
    }
}
