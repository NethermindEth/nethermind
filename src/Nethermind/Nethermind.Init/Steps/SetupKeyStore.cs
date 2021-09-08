//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Network.Config;
using Nethermind.Wallet;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(ResolveIps))]
    public class SetupKeyStore : IStep
    {
        private readonly IApiWithBlockchain _api;

        public SetupKeyStore(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
            (IApiWithStores get, IApiWithBlockchain set) = _api.ForInit;
            // why is the await Task.Run here?
            await Task.Run(() =>
            {
                IKeyStoreConfig keyStoreConfig = get.Config<IKeyStoreConfig>();
                INetworkConfig networkConfig = get.Config<INetworkConfig>();

                AesEncrypter encrypter = new(keyStoreConfig, get.LogManager);

                IKeyStore? keyStore = set.KeyStore = new FileKeyStore(
                    keyStoreConfig,
                    get.EthereumJsonSerializer,
                    encrypter,
                    get.CryptoRandom,
                    get.LogManager,
                    new PrivateKeyStoreIOSettingsProvider(keyStoreConfig));

                set.Wallet = get.Config<IInitConfig>() switch
                {
                    var config when config.EnableUnsecuredDevWallet && config.KeepDevWalletInMemory => new DevWallet(get.Config<IWalletConfig>(), get.LogManager),
                    var config when config.EnableUnsecuredDevWallet && !config.KeepDevWalletInMemory => new DevKeyStoreWallet(get.KeyStore, get.LogManager),
                    _ => new ProtectedKeyStoreWallet(keyStore, new ProtectedPrivateKeyFactory(get.CryptoRandom, get.Timestamper), get.Timestamper, get.LogManager),
                };

                new AccountUnlocker(keyStoreConfig, get.Wallet, get.LogManager, new KeyStorePasswordProvider(keyStoreConfig))
                    .UnlockAccounts();

                BasePasswordProvider passwordProvider = new KeyStorePasswordProvider(keyStoreConfig)
                    .OrReadFromConsole($"Provide password for validator account { keyStoreConfig.BlockAuthorAccount}");
                
                INodeKeyManager nodeKeyManager = new NodeKeyManager(get.CryptoRandom, get.KeyStore, keyStoreConfig, get.LogManager, passwordProvider, get.FileSystem);
                ProtectedPrivateKey? nodeKey = set.NodeKey = nodeKeyManager.LoadNodeKey();

                set.OriginalSignerKey = nodeKeyManager.LoadSignerKey();
                IEnode enode = set.Enode = new Enode(nodeKey.PublicKey, IPAddress.Parse(networkConfig.ExternalIp), networkConfig.P2PPort);
                
                get.LogManager.SetGlobalVariable("enode", enode.ToString());
            }, cancellationToken);
        }
    }
}
