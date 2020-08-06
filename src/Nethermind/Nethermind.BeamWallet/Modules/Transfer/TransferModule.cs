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
        private decimal _estimateGas;
        private decimal _balanceEth;
        private string _value;
        private string _toAddress;
        private string _passphrase;
        private Window _transferWindow;
        private UInt256 _currentNonce;
        private BlockModel<TransactionModel> _latestBlock;
        private long _averageGasPriceNumber;
        private UInt256? _newNonce;
        private Button _backButton;
        private Button _transferButton;
        private Label _txHashLabel;
        private Label _balanceLabel;
        private TransactionModel _transaction;
        private Label _unlockInfoLbl;
        private Label _blockNumberLabel;
        private Label _nonceLabel;
        private Label _estimateGasLabel;
        private Label _sendingTransactionLabel;
        private Label _sentTransactionLabel;
        private Label _lockInfoLabel;
        private UInt256 _averageGasPrice;
        private decimal _transactionValue;
        private TextField _toAddressTextField;

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
        
        private async Task GetAverageGasPriceAsync()
        {
            _blockNumberLabel = new Label(1, 11, "eth_getBlockByNumber: fetching...");
            _transferWindow.Add(_blockNumberLabel);
            RpcResult<BlockModel<TransactionModel>> result;
            do
            {
                result = await _ethJsonRpcClientProxy.eth_getBlockByNumberWithTransactionDetails(
                    BlockParameterModel.Latest,
                    true);
                if (!result.IsValid)
                {
                    await Task.Delay(2000);
                }
            } while (!result.IsValid);
            
            _latestBlock = result.Result;
            UInt256 sum = 0;
            if (_latestBlock.Transactions.Count == 0)
            {
                _averageGasPrice = _gasPrice;
                _transferWindow.Remove(_blockNumberLabel);
                _blockNumberLabel = new Label(1, 14, $"Block number (latest): {_latestBlock.Number}, Average gas price: " +
                                                     $"{_averageGasPrice} WEI");
                _transferWindow.Add(_blockNumberLabel);
            }
            
            int transactionCount = 0;
            foreach (var transaction in _latestBlock.Transactions)
            {
                if (transaction.GasPrice > 100)
                {
                    sum += transaction.GasPrice;
                    transactionCount++;
                }
            }

            _averageGasPriceNumber = transactionCount > 0 ? (long)sum / transactionCount : (long)sum;
            _transferWindow.Remove(_blockNumberLabel);
            _blockNumberLabel = new Label(1, 14, $"Block number (latest): {_latestBlock.Number}, Average gas price: " +
                                                $"{_averageGasPriceNumber} WEI");
            _transferWindow.Add(_blockNumberLabel);
            _averageGasPrice = UInt256.Parse(_averageGasPriceNumber.ToString());
        }

        private async Task GetTransactionCountAsync()
        {
            _nonceLabel = new Label(1, 16, "eth_getTransactionCount: fetching...");
            _transferWindow.Add(_nonceLabel);
            RpcResult<UInt256?> result;
            do
            {
                result = await _ethJsonRpcClientProxy.eth_getTransactionCount(_address);
                if (!result.IsValid)
                {
                    await Task.Delay(3000);
                }
            } while (!result.IsValid || !result.Result.HasValue);
            _currentNonce = result.Result.Value;
            _transferWindow.Remove(_nonceLabel);
            _nonceLabel = new Label(1, 16, $"Nonce: {_currentNonce}");
            _transferWindow.Add(_nonceLabel);
        }

        private async Task GetEstimateGasAsync()
        {
            _estimateGasLabel = new Label(1, 18, "eth_estimateGas: fetching...");
            _transferWindow.Add(_estimateGasLabel);
            
            RpcResult<byte[]> result;
            do
            {
                result = await _ethJsonRpcClientProxy.eth_estimateGas(_transaction,
                    BlockParameterModel.FromNumber(_latestBlock.Number));
                if (!result.IsValid)
                {
                    await Task.Delay(3000);
                }
            } while (!result.IsValid);

            var estimateGasResult = result.Result.ToHexString();
            SetEstimateGas(decimal.Parse(estimateGasResult));
            _transferWindow.Remove(_estimateGasLabel);
            _estimateGasLabel = new Label(1, 18, $"Gas limit: {_estimateGas}");
            _transferWindow.Add(_estimateGasLabel);
        }

        private void SetEstimateGas(decimal estimateGasResult)
        {
            if (estimateGasResult > (decimal) _gasLimit)
            {
                _estimateGas = estimateGasResult;
            }
            else
            {
                _estimateGas = (decimal) _gasLimit;
            }
        }

        public Task<Window> InitAsync()
        {
            _transferWindow = new Window("Transfer") {X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()};
            var fromAddressLabel = new Label(1, 1, "From address:");
            var fromAddressValueLabel = new Label(20, 1, _address.ToString());
            _balanceLabel = new Label(65, 1, $"Balance: {_balanceEth} ETH");
            var toAddressLabel = new Label(1, 3, "To address:");
            _toAddressTextField = new TextField(20, 3, 80, "");
            var valueLabel = new Label(1, 5, "Value [ETH]:");
            var valueTextField = new TextField(20, 5, 80, "");
            var passphraseLabel = new Label(1, 7, "Passphrase:");
            var passphraseTextField = new TextField(20, 7, 80, "");
            passphraseTextField.Secret = true;

            _transferButton = new Button(20, 9, "Transfer");
            _transferButton.Clicked = async () =>
            {
                SetData(_toAddressTextField, valueTextField, passphraseTextField);
                await MakeTransferAsync();
            };
            _backButton = new Button(35, 9, "Back");
            _backButton.Clicked = () =>
            {
                Application.Top.Running = false;
                Application.RequestStop();
            };
            
            var versionInfo = new Label(1, 11, "Beta version, Please check our docs: https://docs.nethermind.io/nethermind/guides-and-helpers/beam-wallet");

            _transferWindow.Add(fromAddressLabel, fromAddressValueLabel, _balanceLabel, toAddressLabel,
                _toAddressTextField, valueLabel, valueTextField, passphraseLabel, passphraseTextField, _transferButton,
                _backButton, versionInfo);

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
            DeleteButtons();
            DeleteLabels();
            
            // personal_unlockAccount here 
            
            if (IncorrectData(_toAddress, _value, _passphrase))
            {
                Application.Run(_transferWindow);
                AddButtons();
                return;
            }

            await GetAverageGasPriceAsync();
            await GetTransactionCountAsync();
            _transactionValue = decimal.Parse(_value);
            Address from = _address;
            Address to = new Address(_toAddress);
            UInt256 value = EthToWei(_value);
            _transaction = CreateTransaction(from, to, value, _gasLimit, _averageGasPrice, _currentNonce);
            await GetEstimateGasAsync();
            decimal txFee = _estimateGas * _averageGasPriceNumber;

            var confirmed = MessageBox.Query(80, 15, "Confirmation",
                $"{Environment.NewLine}" +
                "Do you confirm the transaction?" +
                $"{Environment.NewLine}" +
                $"{Environment.NewLine}" +
                $"From: {_address}" + 
                $"{Environment.NewLine}" +
                $"To: {_toAddress.ToLowerInvariant()}" + 
                $"{Environment.NewLine}" +
                $"Value: {_transactionValue} ETH" +
                $"{Environment.NewLine}" +
                $"Gas limit: {_estimateGas}" +
                $"{Environment.NewLine}" +
                $"Gas price: {WeiToEth(_averageGasPriceNumber)} ETH" +
                $"{Environment.NewLine}" +
                $"Transaction fee: {WeiToEth(txFee)} ETH", "Yes", "No");
            
            if (confirmed == 0)
            {
                _unlockInfoLbl = new Label(1, 20, "personal_unlockAccount: fetching...");
                _transferWindow.Add(_unlockInfoLbl);
            
                RpcResult<bool> unlockAccountResult;
                do
                {
                    unlockAccountResult = await _jsonRpcWalletClientProxy.personal_unlockAccount(_address, _passphrase);
                    if (!unlockAccountResult.IsValid)
                    {
                        await Task.Delay(3000);
                    }
                } while (!unlockAccountResult.IsValid);
                _transferWindow.Remove(_unlockInfoLbl);

                if (!unlockAccountResult.Result)
                {
                    MessageBox.ErrorQuery(40, 8, "Error",
                        $"Unlocking account failed." +
                        $"{Environment.NewLine}Make sure you have pasted your Keystore File into keystore folder." +
                        $"{Environment.NewLine}(ESC to close)");
                    DeleteLabels();
                    AddButtons();
                    Application.Run(_transferWindow);
                }

                _unlockInfoLbl = new Label(1, 20, "Account unlocked.");
                _transferWindow.Add(_unlockInfoLbl);
                
                if (unlockAccountResult.Result)
                {
                    _sendingTransactionLabel =
                        new Label(1, 22, $"Sending transaction with nonce: {_transaction.Nonce}...");
                    _transferWindow.Add(_sendingTransactionLabel);

                    RpcResult<Keccak> sendTransactionResult;
                    do
                    {
                        sendTransactionResult = await _ethJsonRpcClientProxy.eth_sendTransaction(_transaction);
                        if (!sendTransactionResult.IsValid)
                        {
                            await Task.Delay(3000);
                        }
                    } while (!sendTransactionResult.IsValid);

                    do
                    {
                        var newNonceResult = await _ethJsonRpcClientProxy.eth_getTransactionCount(_address);
                        _newNonce = newNonceResult.Result;
                        if (!newNonceResult.IsValid)
                        {
                            await Task.Delay(3000);
                        }
                    } while (_newNonce == _currentNonce);

                    _transferWindow.Add(_transferButton, _backButton);
                    _transferWindow.Remove(_sendingTransactionLabel);

                    _sentTransactionLabel = new Label(1, 22, $"Transaction sent with nonce {_transaction.Nonce}.");
                    _transferWindow.Add(_sentTransactionLabel);
                    if (sendTransactionResult.IsValid)
                    {
                        _txHashLabel = new Label(1, 24, $"Transaction hash: {sendTransactionResult.Result}");
                        _transferWindow.Add(_txHashLabel);
                    }
                    
                    _lockInfoLabel = new Label(1, 26, "personal_lockAccount: fetching...");
                    _transferWindow.Add(_lockInfoLabel);

                    RpcResult<bool> lockAccountResult;
                    do
                    {
                        lockAccountResult = await _jsonRpcWalletClientProxy.personal_lockAccount(_address);
                        if (!lockAccountResult.IsValid)
                        {
                            await Task.Delay(3000);
                        }
                    } while (!lockAccountResult.IsValid);
                    _transferWindow.Remove(_lockInfoLabel);
                    _lockInfoLabel = new Label(1, 26, "Account locked.");
                    _transferWindow.Add(_lockInfoLabel);
                }
            }
            else
            {
                DeleteLabels();
                AddButtons();
            }
        }

        private void AddButtons()
        {
            _transferWindow.Add(_transferButton);
            _transferWindow.Add(_backButton);
        }

        private void DeleteButtons()
        {
            _transferWindow.Remove(_backButton);
            _transferWindow.Remove(_transferButton);
            _transferWindow.SetFocus(_toAddressTextField);
        }

        private void DeleteLabels()
        {
            _transferWindow.Remove(_blockNumberLabel);
            _transferWindow.Remove(_nonceLabel);
            _transferWindow.Remove(_estimateGasLabel);
            _transferWindow.Remove(_unlockInfoLbl);
            _transferWindow.Remove(_sendingTransactionLabel);
            _transferWindow.Remove(_sentTransactionLabel);
            _transferWindow.Remove(_txHashLabel);
            _transferWindow.Remove(_lockInfoLabel);
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

        private bool IncorrectData(string address, string value, string passphrase)
        {
            if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(value) || string.IsNullOrEmpty(passphrase) ||
                !decimal.TryParse(value, out _) || decimal.Parse(value) > _balanceEth)
            {
                MessageBox.ErrorQuery(40, 7, "Error", "Incorrect data." +
                                                      $"{Environment.NewLine}(ESC to close)");
                AddButtons();
                return true;
            }
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
