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
using Nethermind.Core.Crypto;
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
        private TransactionModel _transaction;

        public TransferModule(IEthJsonRpcClientProxy ethJsonRpcClientProxy, IJsonRpcWalletClientProxy
            jsonRpcWalletClientProxy, Address address, decimal balanceEth)
        {
            _ethJsonRpcClientProxy = ethJsonRpcClientProxy;
            _jsonRpcWalletClientProxy = jsonRpcWalletClientProxy;        
            _address = address;
            _balanceEth = balanceEth;
            TimeSpan interval = TimeSpan.FromSeconds(1);
            _timer = new Timer(Update, null, TimeSpan.Zero, interval);
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
            
            var balance = await GetBalanceAsync();
            if (!balance.HasValue)    
            {
                return;
            }

            _balanceEth = balance.Value;
            _transferWindow.Remove(_balanceLabel);
            _balanceLabel = new Label(65, 1, $"Balance: {_balanceEth} ETH");

            _transferWindow.Add(_balanceLabel);
        }
        
        private async Task<decimal?> GetBalanceAsync()
        {
            var result = await _ethJsonRpcClientProxy.eth_getBalance(_address);
            if (!result.IsValid || !result.Result.HasValue)
            {
                return null;
            }
            return WeiToEth(decimal.Parse(result.Result.ToString()));
        }
        
        private async Task<UInt256?> GetAverageGasPriceAsync()
        {
            var infoLbl = new Label(1, 16, "eth_getBlockByNumberWithTransactionDetails: calling...");
            _transferWindow.Add(infoLbl);
            RpcResult<BlockModel<TransactionModel>> result;
            do
            {
                result = await _ethJsonRpcClientProxy.eth_getBlockByNumberWithTransactionDetails(
                    BlockParameterModel.Latest,
                    true);
                await Task.Delay(2000);
            } while (!result.IsValid);

            if (!result.IsValid)
            {
                return null;
            }
            
            _latestBlock = result.Result;
            UInt256 sum = 0;
            int transactionCount = 0;
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
            _transferWindow.Remove(infoLbl);
            infoLbl = new Label(1, 16, $"eth_getBlockByNumberWithTransactionDetails: {_averageGasPriceNumber}");
            _transferWindow.Add(infoLbl);
            return UInt256.Parse(_averageGasPriceNumber.ToString());
        }

        private async Task GetTransactionCountAsync()
        {
            var infoLbl = new Label(1, 17, "eth_getTransactionCount: calling...");
            _transferWindow.Add(infoLbl);
            RpcResult<UInt256?> result;
            do
            {
                result = await _ethJsonRpcClientProxy.eth_getTransactionCount(_address);
                await Task.Delay(3000);
            } while (!result.IsValid || !result.Result.HasValue);
            _currentNonce = result.Result.Value;
            _transferWindow.Remove(infoLbl);
            infoLbl = new Label(1, 17, $"eth_getTransactionCount: {_currentNonce}");
            _transferWindow.Add(infoLbl);
        }

        private async Task GetEstimateGasAsync()
        {
            var infoLbl = new Label(1, 18, "eth_estimateGas: calling...");
            _transferWindow.Add(infoLbl);
            
            RpcResult<byte[]> result;
            do
            {
                result = await _ethJsonRpcClientProxy.eth_estimateGas(_transaction);
                await Task.Delay(3000);
            } while (!result.IsValid);
            _estimateGas = result.Result.ToHexString();
            _transferWindow.Remove(infoLbl);
            infoLbl = new Label(1, 18, $"eth_estimateGas: {_estimateGas}");
            _transferWindow.Add(infoLbl);
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

            var averageGasPrice = await GetAverageGasPriceAsync();
            if (!averageGasPrice.HasValue)
            {
                return;
            }
            
            await GetTransactionCountAsync();
            Address from = _address;
            Address to = new Address(_toAddress);
            UInt256 value = EthToWei(_value);
            _transaction = CreateTransaction(from, to, value, _gasLimit, averageGasPrice.Value, _currentNonce);
            await GetEstimateGasAsync();
            decimal txFee = decimal.Parse(_estimateGas) * _averageGasPriceNumber;

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

                var unlockInfoLbl = new Label(1, 18, "personal_unlockAccount: calling...");
                _transferWindow.Add(unlockInfoLbl);
            
                RpcResult<bool> unlockAccountResult;
                do
                {
                    unlockAccountResult = await _jsonRpcWalletClientProxy.personal_unlockAccount(_address, _passphrase);
                    await Task.Delay(3000);
                } while (!unlockAccountResult.IsValid);
                _transferWindow.Remove(unlockInfoLbl);
                
                unlockInfoLbl = new Label(1, 18, $"personal_unlockAccount: {unlockAccountResult.Result}");
                _transferWindow.Add(unlockInfoLbl);

                if (!unlockAccountResult.Result)
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Unlocking account failed.");
                    Application.Run(_transferWindow);
                }

                _unlockedLabel = new Label(1, 11, "Account unlocked.");
                _transferWindow.Add(_unlockedLabel);
                if (unlockAccountResult.Result)
                {
                    var sendingTransactionLabel =
                        new Label(1, 12, $"Sending transaction with nonce: {_transaction.Nonce}.");
                    _transferWindow.Add(sendingTransactionLabel);


                    RpcResult<Keccak> sendTransactionResult;
                    do
                    {
                        sendTransactionResult = await _ethJsonRpcClientProxy.eth_sendTransaction(_transaction);
                        await Task.Delay(3000);
                    } while (!sendTransactionResult.IsValid);

                    _transferWindow.Remove(unlockInfoLbl);
                    _transferWindow.Remove(_backButton);
                    _transferWindow.Remove(_transferButton);

                    do
                    {
                        var newNonceResult = await _ethJsonRpcClientProxy.eth_getTransactionCount(_address);
                        _newNonce = newNonceResult.Result;
                        await Task.Delay(3000);

                    } while (_newNonce == _currentNonce);

                    _transferWindow.Add(_backButton, _transferButton);
                    _transferWindow.Remove(sendingTransactionLabel);

                    var sentLbl = new Label(1, 12, $"Transaction sent with nonce {_transaction.Nonce}.");
                    _transferWindow.Add(sentLbl);
                    if (sendTransactionResult.IsValid)
                    {
                        _txHashLabel = new Label(1, 13, $"Transaction hash: {sendTransactionResult.Result}");
                        _transferWindow.Add(_txHashLabel);
                    }


                    var lockInfoLbl = new Label(1, 18, "personal_lockAccount: calling...");
                    _transferWindow.Add(lockInfoLbl);

                    RpcResult<bool> lockAccountResult;
                    do
                    {
                        unlockAccountResult = await _jsonRpcWalletClientProxy.personal_lockAccount(_address);
                        await Task.Delay(3000);
                    } while (!unlockAccountResult.IsValid);

                    _transferWindow.Remove(unlockInfoLbl);

                    unlockInfoLbl = new Label(1, 18, "Account locked.");
                    _transferWindow.Add(unlockInfoLbl);
                }
            }
        }

        private void DeleteLabels()
        {
            _transferWindow.Remove(_unlockedLabel);
            _transferWindow.Remove(_txHashLabel);
            _transferWindow.Remove(_accountLockedLabel);
        }

        private static TransactionModel CreateTransaction(Address fromAddress, Address toAddress, UInt256 value, UInt256 gas,
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

        private static UInt256 EthToWei(string result)
        {
            var resultDecimal = decimal.Parse(result);
            var resultRound = decimal.Round(resultDecimal * 1000000000000000000, 0);
            return UInt256.Parse(resultRound.ToString(CultureInfo.InvariantCulture));
        }

        private static decimal WeiToEth(decimal result) => result / 1000000000000000000;
    }
}
