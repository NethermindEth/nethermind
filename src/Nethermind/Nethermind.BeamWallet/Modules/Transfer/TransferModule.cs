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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeamWallet.Clients;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Facade.Proxy;
using Nethermind.Facade.Proxy.Models;
using Terminal.Gui;

namespace Nethermind.BeamWallet.Modules.Transfer
{
    public class TransferModule : IModule
    {
        private readonly Regex _addressRegex = new Regex("(0x)([0-9A-Fa-f]{40})", RegexOptions.Compiled);
        private readonly IEthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private readonly IJsonRpcWalletClientProxy _jsonRpcWalletClientProxy;
        private readonly Address _address;
        private readonly UInt256 _gasPrice = 30000000000;
        private readonly UInt256 _gasLimit = 21000;
        private readonly Timer _timer;
        private const int PositionX = 1;
        private decimal _estimateGas;
        private decimal _balanceEth;
        private string _value;
        private string _toAddress;
        private string _passphrase;
        private long _averageGasPriceNumber;
        private decimal _transactionValue;
        private decimal _transactionFee;
        private Window _window;
        private UInt256 _currentNonce;
        private BlockModel<TransactionModel> _latestBlock;
        private UInt256? _newNonce;
        private Button _backButton;
        private Button _transferButton;
        private Label _balanceLabel;
        private TransactionModel _transaction;
        private Label _unlockFailedLbl;
        private Label _blockNumberLabel;
        private Label _nonceLabel;
        private Label _estimateGasLabel;
        private Label _sendingTransactionLabel;
        private Label _transactionHashLabel;
        private Label _lockInfoLabel;
        private UInt256 _averageGasPrice;
        private TextField _toAddressTextField;
        private Label _unlockInfoLbl;

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
            _window.Remove(_balanceLabel);
            _balanceLabel = new Label(65, 9, $"Balance: {_balanceEth} ETH");
            _window.Add(_balanceLabel);
        }

        private async Task<decimal?> GetBalanceAsync()
        {
            var result = await _ethJsonRpcClientProxy.eth_getBalance(_address);
            if (!result.IsValid || !result.Result.HasValue)
            {
                return null;
            }

            return decimal.Parse(result.Result.ToString()).WeiToEth();
        }

        private async Task GetAverageGasPriceAsync()
        {
            _blockNumberLabel = new Label(PositionX, 19, "eth_getBlockByNumber: fetching...");
            _window.Add(_blockNumberLabel);
            
            _latestBlock = await Extensions.TryExecuteAsync(() =>
                _ethJsonRpcClientProxy.eth_getBlockByNumberWithTransactionDetails(BlockParameterModel.Latest,
                    true));

            UInt256 sum = 0;
            if (_latestBlock.Transactions.Count == 0)
            {
                _averageGasPrice = _gasPrice;
                _window.Remove(_blockNumberLabel);
                _blockNumberLabel = new Label(PositionX, 19, $"Block number (latest): {_latestBlock.Number}, " +
                                                             $"Average gas price: {_averageGasPrice} WEI");
                _window.Add(_blockNumberLabel);
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
            _window.Remove(_blockNumberLabel);
            _blockNumberLabel = new Label(PositionX, 19, $"Block number (latest): {_latestBlock.Number}, " +
                                                         $"Average gas price: {_averageGasPriceNumber} WEI");
            _window.Add(_blockNumberLabel);
            _averageGasPrice = UInt256.Parse(_averageGasPriceNumber.ToString());
        }

        private async Task GetTransactionCountAsync()
        {
            _nonceLabel = new Label(PositionX, 20, "eth_getTransactionCount: fetching...");
            _window.Add(_nonceLabel);
            RpcResult<UInt256?> result;

            var currentNonce = await Extensions.TryExecuteAsync(() =>
                _ethJsonRpcClientProxy.eth_getTransactionCount(_address), rpcResult => !(rpcResult.Result.HasValue));

            _currentNonce = currentNonce.Value;
            _window.Remove(_nonceLabel);
            _nonceLabel = new Label(PositionX, 20, $"Nonce: {_currentNonce}");
            _window.Add(_nonceLabel);
        }

        private async Task GetEstimateGasAsync()
        {
            _estimateGasLabel = new Label(PositionX, 21, "eth_estimateGas: fetching...");
            _window.Add(_estimateGasLabel);

            var gasLimit = await Extensions.TryExecuteAsync(() =>
                _ethJsonRpcClientProxy.eth_estimateGas(_transaction,
                    BlockParameterModel.FromNumber(_latestBlock.Number)));

            SetEstimateGas(gasLimit);
            _window.Remove(_estimateGasLabel);
            _estimateGasLabel = new Label(PositionX, 21, $"Gas limit: {_estimateGas}");
            _window.Add(_estimateGasLabel);
        }

        private void SetEstimateGas(byte[] gasLimit)
        {
            decimal gasLimitResult = decimal.Parse(gasLimit.ToHexString());
            if (gasLimitResult > (decimal)_gasLimit)
            {
                _estimateGas = gasLimitResult;
            }
            else
            {
                _estimateGas = (decimal)_gasLimit;
            }
        }

        public Task<Window> InitAsync()
        {
            CreateWindow();
            AddInfo();

            var fromAddressLabel = new Label(PositionX, 9, "From address:");
            var fromAddressValueLabel = new Label(20, 9, _address.ToString());
            _balanceLabel = new Label(65, 9, $"Balance: {_balanceEth} ETH");
            var toAddressLabel = new Label(PositionX, 11, "To address:");
            _toAddressTextField = new TextField(20, 11, 80, "");
            var valueLabel = new Label(PositionX, 13, "Value [ETH]:");
            var valueTextField = new TextField(20, 13, 80, "");
            var passphraseLabel = new Label(PositionX, 15, "Passphrase:");
            var passphraseTextField = new TextField(20, 15, 80, "");
            passphraseTextField.Secret = true;

            _transferButton = new Button(20, 17, "Transfer");
            _transferButton.Clicked = async () =>
            {
                SetData(_toAddressTextField, valueTextField, passphraseTextField);
                await MakeTransferAsync();
            };
            _backButton = new Button(34, 17, "Back");
            _backButton.Clicked = () =>
            {
                Back();
            };
            var versionInfo = new Label(PositionX, 7, "Beta version, Please check our docs: " +
                                                      "https://docs.nethermind.io/nethermind/nethermind-utilities/beam-wallet");
            _window.Add(fromAddressLabel, fromAddressValueLabel, _balanceLabel, toAddressLabel,
                _toAddressTextField, valueLabel, valueTextField, passphraseLabel, passphraseTextField, _transferButton,
                _backButton, versionInfo);

            return Task.FromResult(_window);
        }

        private void Back()
        {
            Application.Top.Running = false;
            Application.RequestStop();
            Application.Shutdown();
        }

        private void CreateWindow()
        {
            _window = new Window("Beam Wallet") {X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()};
        }

        private void AddInfo()
        {
            var transferInfo = new Label(PositionX, 1, "Transfer ETH." +
                                                       $"{Environment.NewLine}" +
                                                       "- in the first input provide the address to which you want to send ETH" +
                                                       $"{Environment.NewLine}" +
                                                       "- in the input below enter the value of ETH that you want to transfer" +
                                                       $"{Environment.NewLine}" +
                                                       "- and in the last input enter the passphrase of your wallet." +
                                                       $"{Environment.NewLine}{Environment.NewLine}" +
                                                       "This is not the last step, you will be asked to confirm the transaction.");
            _window.Add(transferInfo);
        }

        private void SetData(TextField address, TextField value, TextField passphrase)
        {
            _toAddress = address.Text.ToString();
            _value = value.Text.ToString();
            _passphrase = passphrase.Text.ToString();
        }

        private async Task MakeTransferAsync()
        {
            try
            {
                DeleteButtons();
                DeleteLabels();
                var correctData = await ValidateData(_toAddress, _value);
                if (!correctData)
                {
                    return;
                }

                await GetAverageGasPriceAsync();
                await GetTransactionCountAsync();
                _transactionValue = decimal.Parse(_value);
                Address from = _address;
                Address to = new Address(_toAddress);
                UInt256 value = _value.EthToWei();
                _transaction = CreateTransaction(from, to, value, _gasLimit, _averageGasPrice, _currentNonce);
                await GetEstimateGasAsync();
                _transactionFee = _estimateGas * _averageGasPriceNumber;

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
                    $"Gas price: {_averageGasPriceNumber.WeiToEth()} ETH" +
                    $"{Environment.NewLine}" +
                    $"Transaction fee: {_transactionFee.WeiToEth()} ETH",
                    "Yes", "No");

                if (confirmed != 0)
                {
                    await CancelSendingTransaction();
                    return;
                }

                if (!EnoughFunds() || _balanceEth == 0)
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "You do not have enough funds." +
                                                          $"{Environment.NewLine}(ESC to close)");
                    await CancelSendingTransaction();
                    return;
                }

                _sendingTransactionLabel =
                    new Label(PositionX, 22, $"Sending transaction with nonce: {_transaction.Nonce}...");
                _window.Add(_sendingTransactionLabel);

                var transactionHash = await Extensions.TryExecuteAsync((() =>
                    _ethJsonRpcClientProxy.eth_sendTransaction(_transaction)));

                do
                {
                    var newNonceResult = await _ethJsonRpcClientProxy.eth_getTransactionCount(_address);
                    _newNonce = newNonceResult.Result;
                    await Task.Delay(2000);
                } while (_newNonce == _currentNonce);

                _window.Add(_transferButton, _backButton);
                _window.Remove(_sendingTransactionLabel);

                _transactionHashLabel = new Label(PositionX, 22, "Transaction hash: " +
                                                                 $"{transactionHash}.");
                _window.Add(_transactionHashLabel);
                _lockInfoLabel = new Label(PositionX, 23, "personal_lockAccount: fetching...");
                _window.Add(_lockInfoLabel);

                var accountLocked = await Extensions.TryExecuteAsync((() => 
                    _jsonRpcWalletClientProxy.personal_lockAccount(_address)));
                
                _window.Remove(_lockInfoLabel);
                var accountLockedText = accountLocked ? "Account locked." : "Account has not been locked.";
                _lockInfoLabel = new Label(PositionX, 23, accountLockedText);
                _window.Add(_lockInfoLabel);
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(50, 7, "Error", "There was an error while " +
                                                      "making a transfer. (ESC to close)");
                Back();
            }
        }

        private async Task CancelSendingTransaction()
        {
            await Extensions.TryExecuteAsync((() => _jsonRpcWalletClientProxy.personal_lockAccount(_address)));
            DeleteLabels();
            AddButtons();
        }

        private bool EnoughFunds()
        {
            var transactionCost = _transactionValue + _transactionFee.WeiToEth();
            return _balanceEth >= transactionCost;
        }

        private void AddButtons()
        {
            _window.Add(_transferButton);
            _window.Add(_backButton);
        }

        private void DeleteButtons()
        {
            _window.Remove(_backButton);
            _window.Remove(_transferButton);
            _window.SetFocus(_toAddressTextField);
        }

        private void DeleteLabels()
        {
            _window.Remove(_blockNumberLabel);
            _window.Remove(_nonceLabel);
            _window.Remove(_estimateGasLabel);
            _window.Remove(_unlockFailedLbl);
            _window.Remove(_unlockInfoLbl);
            _window.Remove(_sendingTransactionLabel);
            _window.Remove(_transactionHashLabel);
            _window.Remove(_lockInfoLabel);
        }

        private static TransactionModel CreateTransaction(Address fromAddress, Address toAddress, UInt256 value,
            UInt256 gas, UInt256 averageGasPrice, UInt256 nonce) => new TransactionModel
        {
            From = fromAddress,
            To = toAddress,
            Value = value,
            Gas = gas,
            GasPrice = averageGasPrice,
            Nonce = nonce
        };

        private async Task<bool> ValidateData(string address, string value)
        {
            if (string.IsNullOrEmpty(address) || address.Length != 42 || !_addressRegex.IsMatch(address))
            {
                MessageBox.ErrorQuery(40, 7, "Error", "Address is invalid." +
                                                      $"{Environment.NewLine}(ESC to close)");
                AddButtons();
                return false;
            }

            if (string.IsNullOrEmpty(value) || !decimal.TryParse(value, out _) || decimal.Parse(value) > _balanceEth)
            {
                MessageBox.ErrorQuery(40, 7, "Error", "Incorrect data." +
                                                      $"{Environment.NewLine}(ESC to close)");
                AddButtons();
                return false;
            }

            if (_balanceEth == 0)
            {
                MessageBox.ErrorQuery(40, 7, "Error", "You do not have enough funds." +
                                                      $"{Environment.NewLine}(ESC to close)");
                AddButtons();
                return false;
            }

            return await ValidatePassword();
        }

        private async Task<bool> ValidatePassword()
        {
            if (string.IsNullOrEmpty(_passphrase))
            {
                MessageBox.ErrorQuery(40, 7, "Error", "Passphrase can not be empty." +
                                                      $"{Environment.NewLine}(ESC to close)");
                AddButtons();
                return false;
            }

            _unlockFailedLbl = new Label(PositionX, 18, "personal_unlockAccount: fetching...");
            _window.Add(_unlockFailedLbl);

            var accountUnlocked = await Extensions.TryExecuteAsync(() =>
                _jsonRpcWalletClientProxy.personal_unlockAccount(_address, _passphrase));
            
            _window.Remove(_unlockFailedLbl);

            if (!accountUnlocked)
            {
                MessageBox.ErrorQuery(40, 8, "Error",
                    "Unlocking account failed." +
                    $"{Environment.NewLine}Make sure you have pasted your Keystore File into keystore folder." +
                    $"{Environment.NewLine}(ESC to close)");
                DeleteLabels();
                AddButtons();
                return false;
            }

            _unlockInfoLbl = new Label(PositionX, 18, "Account unlocked.");
            _window.Add(_unlockInfoLbl);

            DeleteButtons();
            return true;
        }
    }
}
