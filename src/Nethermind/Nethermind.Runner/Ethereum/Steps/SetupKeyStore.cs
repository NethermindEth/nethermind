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
            var encrypter = new AesEncrypter(
                _context._configProvider.GetConfig<IKeyStoreConfig>(),
                _context.LogManager);

            _context._keyStore = new FileKeyStore(
                _context._configProvider.GetConfig<IKeyStoreConfig>(),
                _context._ethereumJsonSerializer,
                encrypter,
                _context._cryptoRandom,
                _context.LogManager);

            _context._wallet = _context._initConfig switch
            {
                var config when config.EnableUnsecuredDevWallet && config.KeepDevWalletInMemory
                => new DevWallet(_context._configProvider.GetConfig<IWalletConfig>(), _context.LogManager),
                var config when config.EnableUnsecuredDevWallet && !config.KeepDevWalletInMemory
                => new DevKeyStoreWallet(_context._keyStore, _context.LogManager),
                _ => NullWallet.Instance
            };

            INodeKeyManager nodeKeyManager = new NodeKeyManager(_context._cryptoRandom, _context._keyStore, _context._configProvider.GetConfig<IKeyStoreConfig>(), _context.LogManager);
            _context._nodeKey = nodeKeyManager.LoadNodeKey();
            _context._enode = new Enode(_context._nodeKey.PublicKey, IPAddress.Parse(_context.NetworkConfig.ExternalIp), _context.NetworkConfig.P2PPort);
            
            return Task.CompletedTask;
        }
    }
}