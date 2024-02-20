// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Wallet;

namespace Nethermind.Runner.Modules;

public class KeyStoreModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        builder.RegisterType<FileKeyStore>()
            .As<IKeyStore>()
            .SingleInstance();

        builder.Register<ICryptoRandom, ITimestamper, IKeyStoreConfig, ProtectedPrivateKeyFactory>(CreateProtectedKeyFactory)
            .As<IProtectedPrivateKeyFactory>()
            .SingleInstance();

        builder.Register(CreateWallet)
            .SingleInstance();

        builder.Register(CreateNodeKeyManager)
            .SingleInstance();

        builder.Register<INodeKeyManager, ProtectedPrivateKey>((keyManager) => keyManager.LoadNodeKey())
            .Keyed<ProtectedPrivateKey>(PrivateKeyName.NodeKey)
            .SingleInstance();

        builder.Register<INodeKeyManager, ProtectedPrivateKey>((keyManager) => keyManager.LoadSignerKey())
            .Keyed<ProtectedPrivateKey>(PrivateKeyName.SignerKey)
            .SingleInstance();
    }

    private ProtectedPrivateKeyFactory CreateProtectedKeyFactory(ICryptoRandom cryptoRandom, ITimestamper timeStamper, IKeyStoreConfig keyStoreConfig)
    {
        return new ProtectedPrivateKeyFactory(cryptoRandom, timeStamper, keyStoreConfig.KeyStoreDirectory);
    }

    private IWallet CreateWallet(IComponentContext ctx)
    {
        IWallet wallet = ctx.Resolve<IInitConfig>() switch
        {
            var config when config.EnableUnsecuredDevWallet && config.KeepDevWalletInMemory => ctx.Resolve<DevWallet>(),
            var config when config.EnableUnsecuredDevWallet && !config.KeepDevWalletInMemory => ctx.Resolve<DevKeyStoreWallet>(),
            _ => ctx.Resolve<ProtectedKeyStoreWallet>()
        };

        // Unlock the wallet with KeyStorePasswordProvider. Does not use password from console for some reason.
        IKeyStoreConfig keyStoreConfig = ctx.Resolve<IKeyStoreConfig>();
        ILogManager logManager = ctx.Resolve<ILogManager>();
        new AccountUnlocker(keyStoreConfig, wallet, logManager, new KeyStorePasswordProvider(keyStoreConfig))
            .UnlockAccounts();

        return wallet;
    }

    private INodeKeyManager CreateNodeKeyManager(IComponentContext ctx)
    {
        IKeyStoreConfig keyStoreConfig = ctx.Resolve<IKeyStoreConfig>();

        BasePasswordProvider passwordProvider = new KeyStorePasswordProvider(keyStoreConfig)
            .OrReadFromConsole($"Provide password for validator account {keyStoreConfig.BlockAuthorAccount}");

        // NodeKeyManager need password provider, but it can use console for some reason.
        return ctx.Resolve<NodeKeyManager>(new TypedParameter(typeof(IPasswordProvider), passwordProvider));
    }
}
