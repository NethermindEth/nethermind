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

using System;
using System.IO;
using System.IO.Abstractions;
using System.Net;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Wallet;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies]
    public class SetupKeyStore : IStep
    {
        private readonly EthereumRunnerContext _context;

        public SetupKeyStore(EthereumRunnerContext context)
        {
            _context = context;
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
            // why is the await Task.Run here?
            await Task.Run(() =>
            {
                IKeyStoreConfig keyStoreConfig = _context.Config<IKeyStoreConfig>();
                INetworkConfig networkConfig = _context.Config<INetworkConfig>();

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
                    var config when config.EnableUnsecuredDevWallet && config.KeepDevWalletInMemory => new DevWallet(_context.Config<IWalletConfig>(), _context.LogManager),
                    var config when config.EnableUnsecuredDevWallet && !config.KeepDevWalletInMemory => new DevKeyStoreWallet(_context.KeyStore, _context.LogManager),
                    _ => new ProtectedKeyStoreWallet(_context.KeyStore, new ProtectedPrivateKeyFactory(_context.CryptoRandom, _context.Timestamper), _context.Timestamper, _context.LogManager),
                };

                new AccountUnlocker(keyStoreConfig, _context.Wallet, new FileSystem(), _context.LogManager).UnlockAccounts();

                INodeKeyManager nodeKeyManager = new NodeKeyManager(_context.CryptoRandom, _context.KeyStore, keyStoreConfig, _context.LogManager);
                _context.NodeKey = nodeKeyManager.LoadNodeKey();
                _context.OriginalSignerKey = nodeKeyManager.LoadSignerKey();
                _context.Enode = new Enode(_context.NodeKey.PublicKey, IPAddress.Parse(networkConfig.ExternalIp), networkConfig.P2PPort);
                
                _context.LogManager.SetGlobalVariable("enode", _context.Enode.ToString());
            }, cancellationToken);
        }
    }
}
