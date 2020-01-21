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
using System.Threading.Tasks;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Network;
using Nethermind.Wallet;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class SetupKeyStore : IStep
    {
        private readonly EthereumRunnerContext _context;

        public SetupKeyStore(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute()
        {
            IKeyStoreConfig keyStoreConfig = _context.Config<IKeyStoreConfig>();
            
            AesEncrypter encrypter = new AesEncrypter(
                keyStoreConfig,
                _context.LogManager);

            _context.KeyStore = new FileKeyStore(
                keyStoreConfig,
                _context.EthereumJsonSerializer,
                encrypter,
                _context.CryptoRandom,
                _context.LogManager);

            _context.Wallet = _context.Config<IInitConfig>() switch
            {
                var config when config.EnableUnsecuredDevWallet && config.KeepDevWalletInMemory
                => new DevWallet(_context.Config<IWalletConfig>(), _context.LogManager),
                var config when config.EnableUnsecuredDevWallet && !config.KeepDevWalletInMemory
                => new DevKeyStoreWallet(_context.KeyStore, _context.LogManager),
                _ => NullWallet.Instance
            };

            INodeKeyManager nodeKeyManager = new NodeKeyManager(_context.CryptoRandom, _context.KeyStore, keyStoreConfig, _context.LogManager);
            _context.NodeKey = nodeKeyManager.LoadNodeKey();
            _context.Enode = new Enode(_context.NodeKey.PublicKey, IPAddress.Parse(_context.NetworkConfig.ExternalIp), _context.NetworkConfig.P2PPort);

            return Task.CompletedTask;
        }
    }
}