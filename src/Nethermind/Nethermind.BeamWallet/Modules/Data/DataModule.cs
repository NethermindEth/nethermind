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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeamWallet.Modules.Events;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade.Proxy;
using Terminal.Gui;
using Nethermind.Core;

namespace Nethermind.BeamWallet.Modules.Data
{
    public class DataModule : IModule
    {
        private readonly IEthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private readonly Address _address;
        private decimal _balance;
        private readonly Timer _timer;
        private readonly TimeSpan _interval;
        private Window _window;
        private Label _syncingInfoLabel;
        public event EventHandler<TransferClickedEventArgs> TransferClicked;
        
        public DataModule(IEthJsonRpcClientProxy ethJsonRpcClientProxy, string address)
        {
            _ethJsonRpcClientProxy = ethJsonRpcClientProxy;
            _address = new Address(address);
            _interval = TimeSpan.FromSeconds(5);
            // _timer = new Timer(Update, null, TimeSpan.Zero, _interval);
        }

        public Task<Window> InitAsync()
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

            return Task.FromResult(_window);
        }

        private void Update(object state)
        {
            _ = UpdateBalanceAsync();
        }

        private async Task UpdateBalanceAsync()
        {
            var balanceValueLabel = new Label(70, 1, $"{_balance} ETH {Guid.NewGuid()}");
            if (_syncingInfoLabel is null)
            {
                return;
            }
            
            var balanceResult = await _ethJsonRpcClientProxy.eth_getBalance(_address);
            
            _balance = WeiToEth(balanceResult.Result.ToString());
            _window.Remove(balanceValueLabel);
            balanceValueLabel = new Label(70, 1, $"{_balance} ETH");
            
            _window.Remove(_syncingInfoLabel);
            _window.Add(balanceValueLabel);
        }

        private async Task RenderBalanceAsync()
        {
            var addressLabel = new Label(1, 1, "Address:");
            var addressValueLabel = new Label(15, 1, _address.ToString());
            var balanceLabel = new Label(60, 1, "Balance:");
            _syncingInfoLabel = new Label(70, 1, "Syncing...");
            
            _window.Add(addressLabel, addressValueLabel, balanceLabel, _syncingInfoLabel);
            
            
            
            
            var balanceValueLabel = new Label(70, 1, $"{_balance} ETH");
            var balanceResult = await _ethJsonRpcClientProxy.eth_getBalance(_address);
            
            _balance = WeiToEth(balanceResult.Result.ToString());
            balanceValueLabel = new Label(70, 1, $"{_balance} ETH");
            
            _window.Remove(_syncingInfoLabel);
            _window.Add(balanceValueLabel);
            
            
            
            
            
            var quitButton = new Button(1, 17, "Quit");
            var transferButton = new Button(12, 17, "Transfer");
            
            quitButton.Clicked = () =>
            {
                Application.Top.Running = false;
                Application.RequestStop();
            };

            transferButton.Clicked = () =>
            {
                TransferClicked?.Invoke(this, new TransferClickedEventArgs(_address, _balance));
            };
            
            _window.Add(quitButton, transferButton);

            // var tokenTransaction = new CallTransactionModel();
            // var tokenSignature = "0x70a08231";
            // var tokenData = tokenSignature + "000000000000000000000000" + address.ToString().Substring(2);
            // transaction.Data = Bytes.FromHexString(tokenData);
            //
            // var tokens = InitTokens();
            // var y = 3;
            // foreach (var token in tokens)
            // {
            //     transaction.To = token.Address;
            //     var call = await _ethJsonRpcClientProxy.eth_call(tokenTransaction, BlockParameterModel.Latest);
            //     var resultHex = call.Result.ToHexString();
            //     token.Balance = UInt256.Parse(resultHex, NumberStyles.HexNumber);
            //     var tokenBalanceLbl = new Label(1, y+2, $"{token.Name}:");
            //     var tokenBalance = new Label(15, y+2, $"{ToEth(token.Balance.ToString())}");
            //     window.Add(tokenBalanceLbl, tokenBalance);
            //     y += 2;
            // }

        }

        private static decimal WeiToEth(string result) => (decimal.Parse(result) / 1000000000000000000);

        private static IEnumerable<Token> InitTokens()
        {
            var list = new[]
            {
                new Token("DAI", "0x6b175474e89094c44da98b954eedeac495271d0f"),
                new Token("USDT", "0xdAC17F958D2ee523a2206206994597C13D831ec7"),
                new Token("USDC", "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48"),
                new Token("BAT", "0x0D8775F648430679A709E98d2b0Cb6250d2887EF"),
            };
            return list;
        }

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
    }
}
