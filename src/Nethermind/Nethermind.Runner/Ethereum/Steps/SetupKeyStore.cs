//  Copyright (c) 2018 Demerzel Solutions Limited
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

namespace Nethermind.Runner.Ethereum.Steps
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
            var (_get, _set) = _api.ForInit;
            // why is the await Task.Run here?
            await Task.Run(() =>
            {
                IKeyStoreConfig keyStoreConfig = _get.Config<IKeyStoreConfig>();
                INetworkConfig networkConfig = _get.Config<INetworkConfig>();

                AesEncrypter encrypter = new AesEncrypter(
                    keyStoreConfig,
                    _get.LogManager);

                IKeyStore? keyStore = _set.KeyStore = new FileKeyStore(
                    keyStoreConfig,
                    _get.EthereumJsonSerializer,
                    encrypter,
                    _get.CryptoRandom,
                    _get.LogManager,
                    new PrivateKeyStoreIOSettingsProvider(keyStoreConfig));

                _set.Wallet = _get.Config<IInitConfig>() switch
                {
                    var config when config.EnableUnsecuredDevWallet && config.KeepDevWalletInMemory => new DevWallet(_get.Config<IWalletConfig>(), _get.LogManager),
                    var config when config.EnableUnsecuredDevWallet && !config.KeepDevWalletInMemory => new DevKeyStoreWallet(_get.KeyStore, _get.LogManager),
                    _ => new ProtectedKeyStoreWallet(keyStore, new ProtectedPrivateKeyFactory(_get.CryptoRandom, _get.Timestamper), _get.Timestamper, _get.LogManager),
                };

                new AccountUnlocker(keyStoreConfig, _get.Wallet, _get.LogManager, new KeyStorePasswordProvider(keyStoreConfig))
                    .UnlockAccounts();

                var passwordProvider = new KeyStorePasswordProvider(keyStoreConfig)
                                        .OrReadFromConsole($"Provide password for validator account { keyStoreConfig.BlockAuthorAccount}");
                
                INodeKeyManager nodeKeyManager = new NodeKeyManager(_get.CryptoRandom, _get.KeyStore, keyStoreConfig, _get.LogManager, passwordProvider, _get.FileSystem);
                var nodeKey = _set.NodeKey = nodeKeyManager.LoadNodeKey();

                _set.OriginalSignerKey = nodeKeyManager.LoadSignerKey();
                IEnode enode = _set.Enode = new Enode(nodeKey.PublicKey, IPAddress.Parse(networkConfig.ExternalIp), networkConfig.P2PPort);
                
                _get.LogManager.SetGlobalVariable("enode", enode.ToString());
            }, cancellationToken);
        }
    }
}
