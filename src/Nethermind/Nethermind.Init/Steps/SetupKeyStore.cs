// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Wallet;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies]
    public class SetupKeyStore(INethermindApi api, ICryptoRandom cryptoRandom) : IStep
    {
        public Task Execute(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (IApiWithStores get, IApiWithBlockchain set) = api.ForInit;

            IKeyStoreConfig keyStoreConfig = get.Config<IKeyStoreConfig>();
            INetworkConfig networkConfig = get.Config<INetworkConfig>();

            AesEncrypter encrypter = new(keyStoreConfig, get.LogManager);

            IKeyStore? keyStore = set.KeyStore = new FileKeyStore(
                keyStoreConfig,
                get.EthereumJsonSerializer,
                encrypter,
                cryptoRandom,
                get.LogManager,
                new PrivateKeyStoreIOSettingsProvider(keyStoreConfig));

            set.Wallet = get.Config<IInitConfig>() switch
            {
                { EnableUnsecuredDevWallet: true, KeepDevWalletInMemory: true } => new DevWallet(get.Config<IWalletConfig>(), get.LogManager),
                { EnableUnsecuredDevWallet: true, KeepDevWalletInMemory: false } => new DevKeyStoreWallet(get.KeyStore, get.LogManager),
                _ => new ProtectedKeyStoreWallet(keyStore, new ProtectedPrivateKeyFactory(cryptoRandom, get.Timestamper, keyStoreConfig.KeyStoreDirectory),
                    get.Timestamper, get.LogManager),
            };

            new AccountUnlocker(keyStoreConfig, get.Wallet, get.LogManager, new KeyStorePasswordProvider(keyStoreConfig))
                .UnlockAccounts();

            BasePasswordProvider passwordProvider = new KeyStorePasswordProvider(keyStoreConfig)
                .OrReadFromConsole($"Provide password for validator account {keyStoreConfig.BlockAuthorAccount}");

            INodeKeyManager nodeKeyManager = new NodeKeyManager(cryptoRandom, get.KeyStore, keyStoreConfig, get.LogManager, passwordProvider, get.FileSystem);
            IProtectedPrivateKey? nodeKey = set.NodeKey = nodeKeyManager.LoadNodeKey();

            IMiningConfig miningConfig = get.Config<IMiningConfig>();
            //Don't load the local key if an external signer is configured
            if (string.IsNullOrEmpty(miningConfig.Signer))
            {
                set.OriginalSignerKey = nodeKeyManager.LoadSignerKey();
            }

            networkConfig.Bootnodes = RemoveLocalBootnodes(
                networkConfig.Bootnodes,
                nodeKey.PublicKey,
                get.LogManager.GetClassLogger<SetupKeyStore>());

            return Task.CompletedTask;
        }

        private static string[] RemoveLocalBootnodes(string[] bootnodes, PublicKey localNodeId, ILogger logger)
        {
            if (bootnodes.Length == 0)
            {
                return bootnodes;
            }

            List<string> filteredBootnodes = new(bootnodes.Length);

            for (int i = 0; i < bootnodes.Length; i++)
            {
                string bootnode = bootnodes[i];
                try
                {
                    if (new NetworkNode(bootnode).NodeId != localNodeId)
                    {
                        filteredBootnodes.Add(bootnode);
                    }
                }
                catch (Exception e)
                {
                    if (logger.IsError) logger.Error($"Could not parse enode data from {bootnode}", e);
                    filteredBootnodes.Add(bootnode);
                }
            }

            return [.. filteredBootnodes];
        }
    }
}
