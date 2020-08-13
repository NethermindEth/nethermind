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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeamWallet.Modules.Events;
using Nethermind.Dirichlet.Numerics;
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
        private CallTransactionModel _tokenTransaction;
        private Label _tokenBalanceLabel;
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
            // _ = UpdateTokenBalanceAsync();
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
            AddButtons();
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
        
        private async Task UpdateTokenBalanceAsync()
        {
            if (_tokenBalanceLabel is null)
            {
                return;
            }
        
            var y = 1;
            foreach (var token in _tokens)
            {
                y += 2;
                _tokenTransaction.To = token.Address;
                var tokenBalance = await GetTokenBalanceAsync();
                if (!tokenBalance.HasValue)
                {
                    return;
                }
                token.Balance = tokenBalance.Value;
                _window.Remove(_tokenBalanceLabel);
                _tokenBalanceLabel = new Label(1, y, $"{token.Name}: {WeiToEth(token.Balance)}");
                _window.Add(_tokenBalanceLabel);
            }
        }
        
        private async Task<UInt256?> GetTokenBalanceAsync()
        {
            var counter = 0;
            RpcResult<byte[]> result;
            do
            {
                counter++;
                result = await _ethJsonRpcClientProxy.eth_call(_tokenTransaction, BlockParameterModel.Latest);

            } while (!result.IsValid && counter < 10);
          
            return UInt256.Parse(result.Result.ToHexString(), NumberStyles.HexNumber);
        }

        private async Task RenderBalanceAsync()
        {
            var addressLabel = new Label(1, 1, $"Address: {_address}");
            var balanceLabel = new Label(60, 1, "Balance:");
            _syncingInfoLabel = new Label(70, 1, "Syncing... Please wait for the updated balance.");
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
                _balanceValueLabel = new Label(70, 1, "Syncing... Please wait for the updated balance.");
                return;
            }

            _balanceValueLabel = new Label(70, 1, $"{_balance} ETH (refreshing every 5s).");

            _window.Remove(_syncingInfoLabel);
            _window.Add(_balanceValueLabel);

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

        private async Task DisplayTokensBalance()
        {
            var tokenTransaction = new CallTransactionModel();
            var tokenSignature = "0x70a08231";
            var tokenData = tokenSignature + "000000000000000000000000" + _address.ToString().Substring(2);
            tokenTransaction.Data = Bytes.FromHexString(tokenData);
            _tokenTransaction = tokenTransaction;
            
            var y = 1;
            foreach (var token in _tokens)
            {
                y += 2;
                _tokenTransaction.To = token.Address;
                var tokenBalance = await GetTokenBalanceAsync();
                if (!tokenBalance.HasValue)
                {
                    return;
                }
                token.Balance = tokenBalance.Value;
                _tokenBalanceLabel = new Label(1, y, $"{token.Name}: {WeiToEth(token.Balance)}");
                _window.Add(_tokenBalanceLabel);
            }
        }

        private static decimal WeiToEth(UInt256? result) => (decimal.Parse(result.ToString()) / 1000000000000000000);


        private static IEnumerable<Token> InitTokens()
            => new[]
            {
                new Token("DAI", "0x6b175474e89094c44da98b954eedeac495271d0f"),
                new Token("USDT", "0xdAC17F958D2ee523a2206206994597C13D831ec7"),
                new Token("USDC", "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48"),
                new Token("BAT", "0x0D8775F648430679A709E98d2b0Cb6250d2887EF"),
            };

        private class Token
        {
            public string Name  { get; }
            public Address Address  { get; }
            public UInt256 Balance { get; set; }

            public Token(string name, string address)
            {
                Name = name;
                Address = new Address(address);
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
