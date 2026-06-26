// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Consensus;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Network.Config;
using Nethermind.Wallet;
using System.Linq;

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

            IKeyStore keyStore = new FileKeyStore(
                keyStoreConfig,
                get.EthereumJsonSerializer,
                encrypter,
                cryptoRandom,
                get.LogManager,
                new PrivateKeyStoreIOSettingsProvider(keyStoreConfig));
            set.KeyStore = keyStore;

            IWallet wallet = get.Config<IInitConfig>() switch
            {
                { EnableUnsecuredDevWallet: true, KeepDevWalletInMemory: true } => new DevWallet(get.Config<IWalletConfig>(), get.LogManager),
                { EnableUnsecuredDevWallet: true, KeepDevWalletInMemory: false } => new DevKeyStoreWallet(keyStore, get.LogManager),
                _ => new ProtectedKeyStoreWallet(keyStore, new ProtectedPrivateKeyFactory(cryptoRandom, get.Timestamper, keyStoreConfig.KeyStoreDirectory),
                    get.Timestamper, get.LogManager),
            };
            set.Wallet = wallet;

            new AccountUnlocker(keyStoreConfig, wallet, get.LogManager, new KeyStorePasswordProvider(keyStoreConfig))
                .UnlockAccounts();

            BasePasswordProvider passwordProvider = new KeyStorePasswordProvider(keyStoreConfig)
                .OrReadFromConsole($"Provide password for validator account {keyStoreConfig.BlockAuthorAccount}");

            INodeKeyManager nodeKeyManager = new NodeKeyManager(cryptoRandom, keyStore, keyStoreConfig, get.LogManager, passwordProvider, get.FileSystem);
            IProtectedPrivateKey nodeKey = nodeKeyManager.LoadNodeKey();
            set.NodeKey = nodeKey;

            IMiningConfig miningConfig = get.Config<IMiningConfig>();
            //Don't load the local key if an external signer is configured
            if (string.IsNullOrEmpty(miningConfig.Signer))
            {
                set.OriginalSignerKey = nodeKeyManager.LoadSignerKey();
            }

            networkConfig.Bootnodes = [.. networkConfig.Bootnodes.Where(bn => bn.NodeId != nodeKey.PublicKey)];

            return Task.CompletedTask;
        }
    }
}
