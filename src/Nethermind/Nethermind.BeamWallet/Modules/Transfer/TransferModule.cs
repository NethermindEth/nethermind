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
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeamWallet.Clients;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade.Proxy;
using Nethermind.Facade.Proxy.Models;
using Terminal.Gui;

namespace Nethermind.BeamWallet.Modules.Transfer
{
    public class TransferModule : IModule
    {
        private readonly IEthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private readonly IJsonRpcWalletClientProxy _jsonRpcWalletClientProxy;
        private readonly Address _address;
        private readonly UInt256 _gasPrice = 30000000000;
        private readonly UInt256 _gasLimit = 21000;
        private readonly Timer _timer;
        private readonly TimeSpan _interval;
        private decimal _balanceEth;
        private string _value;
        private string _toAddress;
        private string _passphrase;
        private Window _transferWindow;
        private UInt256 _currentNonce;
        private BlockModel<TransactionModel> _latestBlock;
        private string _estimateGas;
        private long _averageGasPriceNumber;
        private UInt256? _newNonce;
        private Button _backButton;
        private Button _transferButton;
        private Label _unlockedLabel;
        private Label _txHashLabel;
        private Label _accountLockedLabel;
        private Label _balanceLabel;

        public TransferModule(IEthJsonRpcClientProxy ethJsonRpcClientProxy, IJsonRpcWalletClientProxy
            jsonRpcWalletClientProxy, Address address, decimal balanceEth)
        {
            _ethJsonRpcClientProxy = ethJsonRpcClientProxy;
            _jsonRpcWalletClientProxy = jsonRpcWalletClientProxy;        
            _address = address;
            _balanceEth = balanceEth;
            _interval = TimeSpan.FromSeconds(5);
            _timer = new Timer(Update, null, TimeSpan.Zero, _interval);
        }
        
        private void Update(object state)
        {
            _ = UpdateBalanceAsync();
        }
        
        private async Task UpdateBalanceAsync()
        {
            if (_balanceLabel is null)
            {
                return;
            }
            var balanceResult = await _ethJsonRpcClientProxy.eth_getBalance(_address);

            _balanceEth = WeiToEth(decimal.Parse(balanceResult.Result.ToString()));
            _transferWindow.Remove(_balanceLabel);
            _balanceLabel = new Label(65, 1, $"Balance: {_balanceEth} ETH");

            _transferWindow.Add(_balanceLabel);
        }

        public Task<Window> InitAsync()
        {
            _transferWindow = new Window("Transfer") {X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()};
            var fromAddressLabel = new Label(1, 1, "From address:");
            var fromAddressValueLabel = new Label(20, 1, _address.ToString());
            _balanceLabel = new Label(65, 1, $"Balance: {_balanceEth} ETH");
            var toAddressLabel = new Label(1, 3, "To address:");
            var toAddressTextField = new TextField(20, 3, 80, "");
            var valueLabel = new Label(1, 5, "Value [ETH]:");
            var valueTextField = new TextField(20, 5, 80, "");
            var passphraseLabel = new Label(1, 7, "Passphrase:");
            var passphraseTextField = new TextField(20, 7, 80, "");

            _transferButton = new Button(30, 9, "Transfer");
            _transferButton.Clicked = async () =>
            {
                SetData(toAddressTextField, valueTextField, passphraseTextField);
                await MakeTransferAsync();
            };
            _backButton = new Button(20, 9, "Back");
            _backButton.Clicked = () =>
            {
                Application.Top.Running = false;
                Application.RequestStop();
            };

            _transferWindow.Add(fromAddressLabel, fromAddressValueLabel, _balanceLabel, toAddressLabel,
                toAddressTextField, valueLabel, valueTextField, passphraseLabel, passphraseTextField, _backButton,
                _transferButton);

            return Task.FromResult(_transferWindow);
        }

        private void SetData(TextField address, TextField value, TextField passphrase)
        {
            _toAddress = address.Text.ToString();
            _value = value.Text.ToString();
            _passphrase = passphrase.Text.ToString();
        }

        private async Task MakeTransferAsync()
        {
            if (!CorrectData(_toAddress, _value, _passphrase))
            {
                Application.Run(_transferWindow);
                return;
            }
            
            var averageGasPrice = await GetAverageGasPrice();
            var transactionCountResult = await _ethJsonRpcClientProxy.eth_getTransactionCount(_address);
            if (!transactionCountResult.Result.HasValue)
            {
                throw new Exception("There was an error when getting transaction count.");
            }

            _currentNonce = transactionCountResult.Result.Value;
            
            Address from = _address;
            Address to = new Address(_toAddress);
            UInt256 value = EthToWei(_value);
            var transaction = CreateTransaction(from, to, value, _gasLimit, averageGasPrice, _currentNonce);

            var resultGasEstimate = await _ethJsonRpcClientProxy.eth_estimateGas(transaction);
            var txFee = decimal.Zero;
            if (resultGasEstimate.IsValid)
            {
                _estimateGas = resultGasEstimate.Result.ToHexString();
                txFee = decimal.Parse(_estimateGas) * _averageGasPriceNumber;
            }

            var confirmed = MessageBox.Query(40, 10, "Confirmation",
                $"Do you confirm the transaction?" +
                $"{Environment.NewLine}" +
                $"Gas limit: {_estimateGas}" +
                $"{Environment.NewLine}" +
                $"Gas price: {_averageGasPriceNumber}" +
                $"{Environment.NewLine}" +
                $"Transaction fee: {WeiToEth(txFee)}", "Yes", "No");
            
            if (confirmed == 0)
            {
                DeleteLabels();

                var unlockAccountResult = await _jsonRpcWalletClientProxy.personal_unlockAccount(_address, _passphrase);

                if (!unlockAccountResult.Result)
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Unlocking account failed.");
                    Application.Run(_transferWindow);
                }
                
                _unlockedLabel = new Label(1, 11, "Account unlocked.");
                _transferWindow.Add(_unlockedLabel);
                if (unlockAccountResult.Result)
                {
                    var sendingTransactionLabel = new Label(1, 12, $"Sending transaction with nonce {transaction.Nonce}.");
                    var sendTransactionResult = await _ethJsonRpcClientProxy.eth_sendTransaction(transaction);
                    _transferWindow.Add(sendingTransactionLabel);
                    _transferWindow.Remove(_backButton);
                    _transferWindow.Remove(_transferButton);
                    
                    do
                    {
                        var newNoneResult = await _ethJsonRpcClientProxy.eth_getTransactionCount(_address);
                        _newNonce = newNoneResult.Result;
                        
                    } while (_newNonce == _currentNonce);
                    
                    _transferWindow.Add(_backButton, _transferButton);
                    _transferWindow.Remove(sendingTransactionLabel);
                    
                    var sentLbl = new Label(1, 12, $"Transaction sent with nonce {transaction.Nonce}.");
                    _transferWindow.Add(sentLbl);
                    if (sendTransactionResult.IsValid)
                    {
                        _txHashLabel = new Label(1, 13, $"Transaction hash: {sendTransactionResult.Result}");
                        _transferWindow.Add(_txHashLabel);
                    }

                    var resultLockingAccount = await _jsonRpcWalletClientProxy.personal_lockAccount(_address);
                    if (resultLockingAccount.IsValid)
                    {
                        _accountLockedLabel = new Label(1, 14, "Account locked.");
                        _transferWindow.Add(_accountLockedLabel);
                    }
                }
            }
        }

        private void DeleteLabels()
        {
            _transferWindow.Remove(_unlockedLabel);
            _transferWindow.Remove(_txHashLabel);
            _transferWindow.Remove(_accountLockedLabel);
        }

        private TransactionModel CreateTransaction(Address fromAddress, Address toAddress, UInt256 value, UInt256 gas,
            UInt256 averageGasPrice, UInt256 nonce) => new TransactionModel
        {
            From = fromAddress,
            To = toAddress,
            Value = value,
            Gas = gas,
            GasPrice = averageGasPrice,
            Nonce = nonce
        };

        private bool CorrectData(string address, string value, string passphrase)
        {
            if (!string.IsNullOrEmpty(address) && !string.IsNullOrEmpty(value) &&
                !string.IsNullOrEmpty(passphrase) && decimal.TryParse(value, out _) && decimal.Parse(value) < _balanceEth)
            {
                return true;
            }

            MessageBox.ErrorQuery(40, 7, "Error", "Incorrect data.");
            return false;
        }

        private async Task<UInt256> GetAverageGasPrice()
        {
            var resultGetBlockByNumber =
                await _ethJsonRpcClientProxy.eth_getBlockByNumberWithTransactionDetails(BlockParameterModel.Latest,
                    true);
            _latestBlock = resultGetBlockByNumber.Result;
            UInt256 sum = 0;
            var transactionCount = 0;
            if (_latestBlock.Transactions.Count == 0)
            {
                return _gasPrice;
            }

            foreach (var transaction in _latestBlock.Transactions)
            {
                if (transaction.GasPrice > 100)
                {
                    sum += transaction.GasPrice;
                    transactionCount++;
                }
            }

            if (transactionCount == 0)
            {
                return _gasPrice;
            }

            _averageGasPriceNumber = transactionCount > 0 ? (long)sum / transactionCount : (long)sum;
            return UInt256.Parse(_averageGasPriceNumber.ToString());
        }

        private static UInt256 EthToWei(string result)
        {
            var resultDecimal = decimal.Parse(result);
            var resultRound = decimal.Round(resultDecimal * 1000000000000000000, 0);
            return UInt256.Parse(resultRound.ToString(CultureInfo.InvariantCulture));
        }

        private static decimal WeiToEth(decimal result) => result / 1000000000000000000;
    }
}
