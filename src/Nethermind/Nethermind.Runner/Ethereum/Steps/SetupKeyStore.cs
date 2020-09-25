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

using System.IO.Abstractions;
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
    [RunnerStepDependencies]
    public class SetupKeyStore : IStep
    {
        private readonly INethermindApi _api;

        public SetupKeyStore(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
            // why is the await Task.Run here?
            await Task.Run(() =>
            {
                IKeyStoreConfig keyStoreConfig = _api.Config<IKeyStoreConfig>();
                INetworkConfig networkConfig = _api.Config<INetworkConfig>();

                AesEncrypter encrypter = new AesEncrypter(
                    keyStoreConfig,
                    _api.LogManager);

                _api.KeyStore = new FileKeyStore(
                    keyStoreConfig,
                    _api.EthereumJsonSerializer,
                    encrypter,
                    _api.CryptoRandom,
                    _api.LogManager);

                _api.Wallet = _api.Config<IInitConfig>() switch
                {
                    var config when config.EnableUnsecuredDevWallet && config.KeepDevWalletInMemory => new DevWallet(_api.Config<IWalletConfig>(), _api.LogManager),
                    var config when config.EnableUnsecuredDevWallet && !config.KeepDevWalletInMemory => new DevKeyStoreWallet(_api.KeyStore, _api.LogManager),
                    _ => new ProtectedKeyStoreWallet(_api.KeyStore, new ProtectedPrivateKeyFactory(_api.CryptoRandom, _api.Timestamper), _api.Timestamper, _api.LogManager),
                };

                new AccountUnlocker(keyStoreConfig, _api.Wallet, new FileSystem(), _api.LogManager).UnlockAccounts();

                INodeKeyManager nodeKeyManager = new NodeKeyManager(_api.CryptoRandom, _api.KeyStore, keyStoreConfig, _api.LogManager);
                _api.NodeKey = nodeKeyManager.LoadNodeKey();
                _api.OriginalSignerKey = nodeKeyManager.LoadSignerKey();
                _api.Enode = new Enode(_api.NodeKey.PublicKey, IPAddress.Parse(networkConfig.ExternalIp), networkConfig.P2PPort);
                
                _api.LogManager.SetGlobalVariable("enode", _api.Enode.ToString());
            }, cancellationToken);
        }
    }
}
