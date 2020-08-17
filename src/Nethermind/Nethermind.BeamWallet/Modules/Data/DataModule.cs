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
        private Button _skipTokensButton;
        private readonly IEnumerable<Token> _tokens = InitTokens();
        private long? _lastBlockNumber;
        private Button _backButton;
        private Button _transferButton;
        private Label _tokensSyncingInfoLabel;

        public event EventHandler<TransferClickedEventArgs> TransferClicked;

        public DataModule(IEthJsonRpcClientProxy ethJsonRpcClientProxy, string address)
        {
            _ethJsonRpcClientProxy = ethJsonRpcClientProxy;
            _address = new Address(address);
            _timer = new Timer(Update, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
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
            InitButtons();
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
            _balanceValueLabel = new Label(70, 1, $"{_balance} ETH");
            _window.Remove(_syncingInfoLabel);
            _window.Add(_balanceValueLabel);
        }

        private void InitButtons()
        {
            _transferButton = new Button(10, 11, "Transfer");
            _transferButton.Clicked = () =>
            {
                TransferClicked?.Invoke(this, new TransferClickedEventArgs(_address, _balance));
            };
            
            _skipTokensButton = new Button(10, 11, "Skip getting token balance and transfer");
            _skipTokensButton.Clicked = () =>
            {
                _window.Add(_transferButton);
                _window.Remove(_skipTokensButton);
                _window.Remove(_tokensSyncingInfoLabel);
                TransferClicked?.Invoke(this, new TransferClickedEventArgs(_address, _balance));
            };
            
            _backButton = new Button(1, 11, "Back");
            _backButton.Clicked = () =>
            {
                Application.Top.Running = false;
                Application.RequestStop();
            };
            _window.Add(_backButton);
        }

        private async Task SetLatestBlockNumber()
        {
            RpcResult<long?> result;
            do
            {
                result = await _ethJsonRpcClientProxy.eth_blockNumber();
                if (!result.IsValid)
                {
                    await Task.Delay(1000);
                }
            } while (!result.IsValid || !result.Result.HasValue || result.Result == 0);

            _lastBlockNumber = result.Result.Value;
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
                result = await _ethJsonRpcClientProxy.eth_call(GetTransactionModel(token),
                    _lastBlockNumber.HasValue
                        ? BlockParameterModel.FromNumber(_lastBlockNumber.Value)
                        : BlockParameterModel.Latest);
            } while (!result.IsValid);

            return result.IsValid
                ? new Token(token.Name, token.Address)
                {
                    Balance = UInt256.Parse(result.Result.ToHexString(), NumberStyles.HexNumber)
                }
                : null;
        }

        private async Task<long?> GetBlockNumber()
        {
            var result = await _ethJsonRpcClientProxy.eth_blockNumber();
            return result.Result;
        }

        private async Task RenderBalanceAsync()
        {
            var addressLabel = new Label(1, 1, $"Address: {_address}");
            var balanceLabel = new Label(60, 1, "Balance:");
            _syncingInfoLabel = new Label(70, 1, "Syncing... Please wait for the balance. " +
                                                 "This may take up to 10min.");
            _window.Add(addressLabel, balanceLabel, _syncingInfoLabel);

            decimal? balance;
            do
            {
                balance = await GetBalanceAsync();
                await Task.Delay(1000);
            } while (!balance.HasValue);

            _balance = balance.Value;
            if (await GetBlockNumber() == 0)
            {
                _balanceValueLabel = new Label(70, 1, "Syncing... Please wait for the balance." +
                                                      "This may take up to 10min.");
                return;
            }

            _balanceValueLabel = new Label(70, 1, $"{_balance} ETH");

            _window.Remove(_syncingInfoLabel);
            _window.Add(_balanceValueLabel);
            _tokensSyncingInfoLabel = new Label(1, 3, "Tokens balance syncing...");
            var netVersionResult = await _ethJsonRpcClientProxy.net_version();
            if (netVersionResult.Result == "1")
            {
                _window.Add(_skipTokensButton);
                _window.Add(_tokensSyncingInfoLabel);
                await SetLatestBlockNumber();
                await GetTokensBalanceAsync();
                _window.Remove(_tokensSyncingInfoLabel);
                _window.Remove(_skipTokensButton);
            }
            _window.Add(_transferButton);
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
                // new Token("BAT", new Address("0x0D8775F648430679A709E98d2b0Cb6250d2887EF"))
            };

        private class Token
        {
            public string Name { get; }
            public Address Address { get; }
            public UInt256 Balance { get; set; }
            public Label Label { get; set; }

            public Token(string name, Address address)
            {
                Name = name;
                Address = address;
            }
        }
    }
}
