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
        private decimal _balanceEth;
        private readonly UInt256 _gasPrice = 30000000000;
        private readonly UInt256 _gasLimit = 21000;
        private Window _transferWindow;
        private TextField _valueTextField;
        private TextField _passphraseTextField;
        private TextField _toAddressTextField;
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
        private UInt256 _valueWei;
        private Label _balanceValueLabel;

        public TransferModule(IEthJsonRpcClientProxy ethJsonRpcClientProxy, IJsonRpcWalletClientProxy
            jsonRpcWalletClientProxy, Address address, decimal balanceEth)
        {
            _ethJsonRpcClientProxy = ethJsonRpcClientProxy;
            _jsonRpcWalletClientProxy = jsonRpcWalletClientProxy;
            _address = address;
            _balanceEth = balanceEth;
        }

        public async Task<Window> InitAsync()
        {
            _transferWindow = new Window("Transfer") {X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()};

            var fromAddressLabel = new Label(1, 1, "From address:");
            var fromAddressValueLabel = new Label(20, 1, _address.ToString());

            var balanceLabel = new Label(65, 1, "Balance");
            _balanceValueLabel = new Label(73, 1, $"{_balanceEth} ETH");

            var toAddressLabel = new Label(1, 3, "To address:");
            _toAddressTextField = new TextField(20, 3, 80, "");

            var valueLabel = new Label(1, 5, "Value [ETH]:");
            _valueTextField = new TextField(20, 5, 80, "");

            var passphraseLabel = new Label(1, 7, "Passphrase:");
            _passphraseTextField = new TextField(20, 7, 80, "");
            _transferButton = new Button(30, 9, "Transfer");

            _transferButton.Clicked = async () =>
            {
                await MakeTransferAsync();
            };
            _backButton = new Button(20, 9, "Back");
            _backButton.Clicked = () =>
            {
                Application.Top.Running = false;
                Application.RequestStop();
            };

            _transferWindow.Add(fromAddressLabel, fromAddressValueLabel, balanceLabel, _balanceValueLabel,
                toAddressLabel, _toAddressTextField, valueLabel, _valueTextField, passphraseLabel,
                _passphraseTextField, _backButton, _transferButton);

            return _transferWindow;
        }

        private async Task MakeTransferAsync()
        {
            var averageGasPrice = await GetAverageGasPrice();
            var resultTransactionCount = await _ethJsonRpcClientProxy.eth_getTransactionCount(_address);
            if (!resultTransactionCount.IsValid)
            {
                throw new Exception("There was an error when getting transaction count.");
            }

            _currentNonce = (UInt256)resultTransactionCount.Result;
                    
            Address from = _address;
            Address to = new Address(_toAddressTextField.Text.ToString());
            _valueWei = EthToWei(_valueTextField.Text.ToString());
            UInt256 value = _valueWei;
            var transaction = CreateTransaction(from, to, value, _gasLimit, averageGasPrice, _currentNonce);

            var resultGasEstimate = await _ethJsonRpcClientProxy.eth_estimateGas(transaction);
            var txFee = decimal.Zero;
            if (resultGasEstimate.IsValid)
            {
                _estimateGas = resultGasEstimate.Result.ToHexString();
                txFee = decimal.Parse(_estimateGas) * _averageGasPriceNumber;
            }
            if (!CorrectData(_toAddressTextField, _valueTextField, _passphraseTextField))
            {
                Application.Run(_transferWindow);
                return;
            }
            var confirmed = MessageBox.Query(50, 15, "Confirmation",
                $"Do you confirm the transaction?" +
                $"{Environment.NewLine}" +
                $"Gas limit: {_estimateGas}" +
                $"{Environment.NewLine}" +
                $"Gas price: {_averageGasPriceNumber}" +
                $"{Environment.NewLine}" +
                $"Transaction fee: {WeiToEth(txFee)}", "Yes", "No");
            
            if (confirmed == 0)
            {
                _transferWindow.Remove(_unlockedLabel);
                _transferWindow.Remove(_txHashLabel);
                _transferWindow.Remove(_accountLockedLabel);

                var resultUnlockAccount = await _jsonRpcWalletClientProxy.personal_unlockAccount(_address,
                    _passphraseTextField.Text.ToString());

                if (!resultUnlockAccount.Result)
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Unlocking account failed.");
                    Application.Run(_transferWindow);
                }
                
                _unlockedLabel = new Label(1, 11, "Account unlocked.");
                _transferWindow.Add(_unlockedLabel);
                if (resultUnlockAccount.Result)
                {
                    var sendingLbl = new Label(1, 12, $"Sending transaction with nonce {transaction.Nonce}.");
                    var resultSendTransaction = await _ethJsonRpcClientProxy.eth_sendTransaction(transaction);
                    _transferWindow.Add(sendingLbl);
                    _transferWindow.Remove(_backButton);
                    _transferWindow.Remove(_transferButton);
                    
                    do
                    {
                        var newNoneResult = await _ethJsonRpcClientProxy.eth_getTransactionCount(_address);
                        _newNonce = newNoneResult.Result;
                    } while (_newNonce == _currentNonce);
                    
                    _transferWindow.Add(_backButton, _transferButton);
                    _transferWindow.Remove(sendingLbl);
                    
                    
                    var resultGetBalance = await _ethJsonRpcClientProxy.eth_getBalance(_address);
                    var balanceEth = WeiToEth(resultGetBalance.Result.ToString());
                    _balanceEth = balanceEth;
                    _transferWindow.Add(_balanceValueLabel);
                    
                    var sentLbl = new Label(1, 12, $"Transaction sent with nonce {transaction.Nonce}.");
                    _transferWindow.Add(sentLbl);
                    if (resultSendTransaction.IsValid)
                    {
                        _txHashLabel = new Label(1, 13, $"Transaction hash: {resultSendTransaction.Result}");
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

        private bool CorrectData(TextField address, TextField value, TextField passphrase)
        {
            var valueWei = WeiToEth((decimal)_valueWei);
            if (!string.IsNullOrEmpty(address.Text.ToString()) && !string.IsNullOrEmpty(value.Text.ToString()) &&
                !string.IsNullOrEmpty(passphrase.Text.ToString()) &&
                decimal.TryParse(value.Text.ToString(), out _) && valueWei < _balanceEth)
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
            var transactions = _latestBlock.Transactions;
            UInt256 sum = 0;
            var transactionCount = 0;
            if (transactions.Count == 0)
            {
                return _gasPrice;
            }

            foreach (var transaction in transactions)
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
            return UInt256.Parse(resultRound.ToString());
        }

        private decimal WeiToEth(decimal result)
        {
            return result / 1000000000000000000;
        }

        private static decimal WeiToEth(string result)
        {
            return (decimal.Parse(result) / 1000000000000000000);
        }
    }
}
