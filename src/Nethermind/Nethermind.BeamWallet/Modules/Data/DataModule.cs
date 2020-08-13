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
// 

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeamWallet.Modules.Events;
using Nethermind.Int256;
using Nethermind.Facade.Proxy;
using Terminal.Gui;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Proxy.Models;

namespace Nethermind.BeamWallet.Modules.Data
{
    public class DataModule : IModule
    {
        private readonly IEthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private readonly Address _address;
        private readonly Timer _timer;
        private decimal _balance;
        private Window _window;
        private Label _syncingInfoLabel;
        private Label _balanceValueLabel;
        private readonly IEnumerable<Token> _tokens = InitTokens();
        private readonly Process _process;
        private bool _externalRunnerIsRunning;

        public event EventHandler<TransferClickedEventArgs> TransferClicked;
        
        public DataModule(IEthJsonRpcClientProxy ethJsonRpcClientProxy, string address, Process process, bool externalRunnerIsRunning)
        {
            _externalRunnerIsRunning = externalRunnerIsRunning;
            _ethJsonRpcClientProxy = ethJsonRpcClientProxy;
            _address = new Address(address);
            _process = process;
            _timer = new Timer(Update, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        public async Task<Window> InitAsync()
        {
            _window = new Window("Data")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),    
                Height = Dim.Fill()
            };
            Application.Top.Add(_window);
            RenderBalanceAsync();

            return _window;
        }

        private void Update(object state)
        {
            _ = UpdateBalanceAsync();
        }

        private async Task UpdateBalanceAsync()
        {
            if (_syncingInfoLabel is null || _balanceValueLabel is null || _address is null)
            {
                return;
            }

            var balance = await GetBalanceAsync();
            if (!balance.HasValue || balance.Value == 0)    
            {
                return;
            }

            _balance = balance.Value;
            _window.Remove(_balanceValueLabel);
            _balanceValueLabel = new Label(70, 1, $"{_balance} ETH (refreshing every 5s).");
            _window.Remove(_syncingInfoLabel);
            _window.Add(_balanceValueLabel);
        }

        private async Task<long?> GetLatestBlockNumber()
        {
            var result = await _ethJsonRpcClientProxy.eth_blockNumber();
            return result.Result;
        }

        private async Task<decimal?> GetBalanceAsync()
        {
            var result = await _ethJsonRpcClientProxy.eth_getBalance(_address);
            if (!result.IsValid || !result.Result.HasValue)
            {
                return null;
            }

            return WeiToEth(result.Result);
            }
        
        private async Task GetTokensBalanceAsync()
        {
            var position = 1;
            var tasks = _tokens.Select(GetTokenBalanceAsync);
            foreach (var token in _tokens)
            {
                if (token.Label is {})
                {
                    _window.Remove(token.Label);
                }
            }
            var tokens = await Task.WhenAll(tasks);
            foreach (var token in tokens.Where(t => t is {}))
            {
                position += 2;
                if (token.Label is {})
                {
                    _window.Remove(token.Label);
                }
                token.Label = new Label(1, position, $"{token.Name}: {token.Balance}");
                _window.Add(token.Label);
            }
        }

        private async Task<Token> GetTokenBalanceAsync(Token token)
        {
            RpcResult<byte[]> result;
            do
            {
                result = await _ethJsonRpcClientProxy.eth_call(GetTransactionModel(token), BlockParameterModel.Latest);
            } while (!result.IsValid);

            return result.IsValid
                ? new Token(token.Name, token.Address)
                {
                    Balance = UInt256.Parse(result.Result.ToHexString(), NumberStyles.HexNumber)
                }
                : null;
        }

        private async Task RenderBalanceAsync()
        {
            var addressLabel = new Label(1, 1, $"Address: {_address}");
            var balanceLabel = new Label(60, 1, "Balance:");
            _syncingInfoLabel = new Label(70, 1, "Syncing... Please wait for the updated balance. " +
                                                 "This may take up to 10min");
            _window.Add(addressLabel, balanceLabel, _syncingInfoLabel);

            decimal? balance;
            do
            {
                balance = await GetBalanceAsync();
                await Task.Delay(1000);
            } while (!balance.HasValue);

            _balance = balance.Value;
            if (await GetLatestBlockNumber() == 0)
            {
                _balanceValueLabel = new Label(70, 1, "Syncing... Please wait for the updated balance." +
                                                      "This may take up to 10min");
                return;
            }

            _balanceValueLabel = new Label(70, 1, $"{_balance} ETH (refreshing every 5s).");

            _window.Remove(_syncingInfoLabel);
            _window.Add(_balanceValueLabel);
            var tokensSyncingInfoLabel = new Label(1, 3, "Tokens balance syncing...");
            _window.Add(tokensSyncingInfoLabel);
            await GetTokensBalanceAsync(); // add if - only when mainnet, admin.nodeInfo -> network - Mainnet: 1
            _window.Remove(tokensSyncingInfoLabel);
            AddButtons();
        }

        private void AddButtons()
        {
            var transferButton = new Button(1, 11, "Transfer");
            transferButton.Clicked = () =>
            {
                TransferClicked?.Invoke(this, new TransferClickedEventArgs(_address, _balance));
            };

            var quitButton = new Button(15, 11, "Quit");
            quitButton.Clicked = () =>
            {
                if (!_externalRunnerIsRunning)
                {
                    CloseAppWithRunner();
                }

                Application.Top.Running = false;
                Application.RequestStop();
            };
            _window.Add(transferButton, quitButton);
        }

        private CallTransactionModel GetTransactionModel(Token token)
        {
            var tokenTransaction = new CallTransactionModel();
            var tokenSignature = "0x70a08231";
            var tokenData = tokenSignature + "000000000000000000000000" + _address.ToString().Substring(2);
            tokenTransaction.To = token.Address;
            tokenTransaction.Data = Bytes.FromHexString(tokenData);

            return tokenTransaction;
        }

        private static decimal WeiToEth(UInt256? result) => (decimal.Parse(result.ToString()) / 1000000000000000000);

        private static IEnumerable<Token> InitTokens()
            => new[]
            {
                new Token("DAI", new Address("0x6b175474e89094c44da98b954eedeac495271d0f")),
                new Token("USDT", new Address("0xdAC17F958D2ee523a2206206994597C13D831ec7")),
                new Token("USDC", new Address("0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48")),
                new Token("BAT", new Address("0x0D8775F648430679A709E98d2b0Cb6250d2887EF"))
            };

        private class Token
        {
            public string Name  { get; }
            public Address Address  { get; }
            public UInt256 Balance { get; set; }
            public Label Label { get; set; }

            public Token(string name, Address address)
            {
                Name = name;
                Address = address;
            }
        }
        
        private void CloseAppWithRunner()
        {
            var confirmed = MessageBox.Query(80, 8, "Confirmation",
                $"{Environment.NewLine}" +
                "Nethermind.Runner is running in the background. Do you want to stop it?", "Yes", "No");

            if (confirmed == 0)
            {
                try
                {
                    _process.Kill();
                }
                catch
                {
                    // ignored
                }
            }
        }
    }
}
