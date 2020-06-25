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
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade.Proxy;
using Nethermind.Facade.Proxy.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Terminal.Gui;

namespace Nethermind.BeamSyncWallet.Modules.Data
{
    public class DataModule : IModule
    {
        private readonly IEthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private readonly string _address;
        
        public DataModule(IEthJsonRpcClientProxy ethJsonRpcClientProxy, string address)
        {
            _ethJsonRpcClientProxy = ethJsonRpcClientProxy;
            _address = address;
        }
        
        public async Task<Window> InitAsync()
        {
            var window = new Window("Data")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };
            Application.Top.Add(window);
            
            
            var address = new Address(_address);
            var balance = await _ethJsonRpcClientProxy.eth_getBalance(address);

            var quitBtn = new Button(1, 17, "Quit");
            
            var enterAddressBtb = new Button(12, 17, "Enter another address");
            var transferBtn = new Button(40, 17, "Transfer");
            
            quitBtn.Clicked = () =>
            {
                Application.Top.Running = false;
                Application.Shutdown();
                Application.RequestStop();
            };
            enterAddressBtb.Clicked = () =>
            {
                window.FocusFirst();
                Application.Run();
            };
            transferBtn.Clicked = () =>
            {
                var transferWindow = new Window("Transfer")
                {
                    X = 0,
                    Y = 0,
                    Width = Dim.Fill(),
                    Height = Dim.Fill()
                };
                Application.Top.Add(transferWindow);
                var fromAddressLbl = new Label(1, 1, "From address:");
                var fromAddressValueLbl = new Label(15, 1, address.ToString());
                var balanceLbl = new Label(60, 1, "Balance:");
                var balanceValueLbl = new Label(70, 1, $"{ToEth(balance.Result.ToString())} ETH");
                
                var toAddressLbl = new Label(1, 3, "To address:");
                var toAddressTxtField = new TextField(20, 3, 80, "");
                
                var valueLbl = new Label(1, 5, "Value [ETH]:");
                var valueTxtField = new TextField(20, 5, 80, "");
                
                var transferBtn = new Button(30, 7, "Transfer");
                var backBtn = new Button(20, 7, "Back");
                backBtn.Clicked = () =>
                {
                    transferWindow.FocusPrev();
                    Application.Run();
                    
                };
                
                transferWindow.Add(fromAddressLbl, fromAddressValueLbl, balanceLbl, balanceValueLbl,
                    toAddressLbl, toAddressTxtField, valueLbl, valueTxtField, backBtn, transferBtn);

                Application.Run(transferWindow);
            };
            window.Add(quitBtn, enterAddressBtb, transferBtn);
            
            var transaction = new CallTransactionModel();
            var signature = "0x70a08231";
            var data = signature + "000000000000000000000000" + address.ToString().Substring(2);
            transaction.Data = Bytes.FromHexString(data);

            var tokens = InitTokens();
            var y = 3;
            foreach (var token in tokens)
            {
                transaction.To = token.Address;
                var call = await _ethJsonRpcClientProxy.eth_call(transaction, BlockParameterModel.Latest);
                var resultHex = call.Result.ToHexString();
                token.Balance = UInt256.Parse(resultHex, NumberStyles.HexNumber);
                var tokenBalanceLbl = new Label(1, y+2, $"{token.Name}:");
                var tokenBalance = new Label(15, y+2, $"{ToEth(token.Balance.ToString())}");
                window.Add(tokenBalanceLbl, tokenBalance);
                y += 2;
            }
            
            var addressLbl = new Label(1, 1, "Address:");
            var addressValueLbl = new Label(15, 1, address.ToString());
            var balanceLbl = new Label(1, 3, "Balance:");
            var balanceValueLbl = new Label(15, 3, $"{ToEth(balance.Result.ToString())} ETH");
            
            window.Add(addressLbl, addressValueLbl, balanceLbl, balanceValueLbl);
            return window;
        }

        private static decimal ToEth(string result) => (decimal.Parse(result) / 1000000000000000000);

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
