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
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace Nethermind.BeamWallet.Modules.Addresses
{
    internal class AddressesModule : IModule
    {
        private readonly Regex _urlRegex = new Regex(@"^http(s)?://([\w-]+.)+[\w-]+(/[\w- ./?%&=])?",
            RegexOptions.Compiled);
        private readonly Regex _addressRegex = new Regex("(0x)([0-9A-Fa-f]{40})", RegexOptions.Compiled);
        private Process _process;
        private Timer _timer;
        private Window _mainWindow;
        private int _processId;
        private Label _runnerOnInfo;
        private Label _runnerOffInfo;
        private EthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private bool _externalRunnerIsRunning;
        private const string DefaultUrl = "http://localhost:8545";
        private const string FileName = "Nethermind.Runner";

        public event EventHandler<(string nodeAddress, string address, Process process, bool _externalRunnerIsRunning)>
            AddressesSelected;

        public AddressesModule()
        {
            // if (!File.Exists(path))
            // {
            //     return;
            // }
            InitData();
            CreateWindow();
            CreateProcess();
            StartProcess();
        }

        private void InitData()
        {
            var httpClient = new HttpClient();
            var urls = new[] {DefaultUrl};
            var jsonRpcClientProxy = new JsonRpcClientProxy(new DefaultHttpClient(httpClient,
                new EthereumJsonSerializer(), LimboLogs.Instance, 0), urls, LimboLogs.Instance);
            _ethJsonRpcClientProxy = new EthJsonRpcClientProxy(jsonRpcClientProxy);
        }

        private void CreateWindow()
        {
            _mainWindow = new Window("Beam Wallet")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };
        }

        private void CreateProcess()
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = GetFileName(),
                    Arguments = "--config mainnet_beam --JsonRpc.Enabled true",
                    RedirectStandardOutput = true
                }
            };
        }

        private static string GetFileName()
            => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{FileName}.exe" : $"./{FileName}";

        private async Task StartProcess()
        {
            AddInfo();
            AddRunnerInfo("Launching Nethermind.Runner...");

            var runnerIsRunning = await CheckIsProcessRunning();
            if (runnerIsRunning)
            {
                _externalRunnerIsRunning = true;
                AddRunnerInfo("Nethermind Runner is already running.");
                return;
            }

            try
            {
                _externalRunnerIsRunning = false;
                _process.Start();
                _processId = _process.Id;
                _timer = new Timer(Update, null, TimeSpan.Zero, TimeSpan.FromSeconds(8));
            }
            catch
            {
                AddRunnerInfo("Error with starting a Nethermind.Runner process.");
            }
        }

        private async Task<bool> CheckIsProcessRunning()
        {
            var result = await _ethJsonRpcClientProxy.eth_blockNumber();
            return result?.IsValid is true;
        }

        private void Update(object state)
        {
            UpdateRunnerState();
        }

        private void UpdateRunnerState()
        {
            Process process = null;
            try
            {
                process = Process.GetProcessById(_processId);
                AddRunnerInfo("Nethermind Runner is running.");
                return;
            }
            catch
            {
                // ignored
            }

            if (process is null)
            {
                if (_runnerOnInfo is {})
                {
                    _mainWindow.Remove(_runnerOnInfo);
                }

                _runnerOffInfo = new Label(3, 20, $"Nethermind Runner is stopped.. Please, wait for it to start.");
                _mainWindow.Add(_runnerOffInfo);
                _process.Start();
                _processId = _process.Id;
            }

            if (_runnerOffInfo is {})
            {
                _mainWindow.Remove(_runnerOffInfo);
            }

            _runnerOnInfo = new Label(3, 20, "Nethermind Runner is running.");
            _mainWindow.Add(_runnerOnInfo);
        }
        private void AddInfo()
        {
            var beamWalletInfo = new Label(3, 1, "Hello, Welcome to Nethermind Beam Wallet - a simple " +
                                                 "console application that allows you to easily" +
                                                 $"{Environment.NewLine}" +
                                                 "and quickly make transactions on Mainnet Ethereum." +
                                                 $"{Environment.NewLine}" +
                                                 "To get started you will need:" +
                                                 $"{Environment.NewLine}" +
                                                 "- your wallet address" +
                                                 $"{Environment.NewLine}" +
                                                 "- passphrase to your wallet" +
                                                 $"{Environment.NewLine}" +
                                                 "- keystore file" +
                                                 $"{Environment.NewLine}" +
                                                 "- and the address to which you want to transfer ETH." +
                                                 $"{Environment.NewLine}{Environment.NewLine}" +
                                                 "Before we start, please copy keystore file of your account into " +
                                                 "folder 'keystore' - this is" +
                                                 $"{Environment.NewLine}" +
                                                 "necessary to properly unlock the account before making a transaction." +
                                                 $"{Environment.NewLine}{Environment.NewLine}" +
                                                 "To navigate through the application - use the TAB key or Up and Down arrows.");
            
            var betaVersionWarningInfo = new Label(3, 14, "This is a Beta version, so for your own safety please, do " +
                                                         "not use an account with a high balance.");

            var warningInfo = new Label(3, 16, "There are a few things that could have gone wrong:" +
                                              $"{Environment.NewLine}" +
                                              "- your balance may be incorrect" +
                                              $"{Environment.NewLine}" +
                                              "- the transaction fee may be charged incorrectly");
            
            betaVersionWarningInfo.TextColor = new Attribute();
            
            _mainWindow.Add(betaVersionWarningInfo, warningInfo, beamWalletInfo);
        }

        private void AddRunnerInfo(string info)
        {
            if (_runnerOnInfo is {})
            {
                _mainWindow.Remove(_runnerOnInfo);
            }

            _runnerOnInfo = new Label(3, 20, $"{info}");
            _mainWindow.Add(_runnerOnInfo);
        }

        public Task<Window> InitAsync()
        {
            var nodeAddressLabel = new Label(3, 22, "Enter node address:");
            var nodeAddressTextField = new TextField(28, 22, 80, $"{DefaultUrl}");
            var addressLabel = new Label(3, 24, "Enter account address:");
            var addressTextField = new TextField(28, 24, 80, "");

            var okButton = new Button(28, 26, "OK");
            var quitButton = new Button(36, 26, "Quit");
            quitButton.Clicked = () =>
            {
                if (!_externalRunnerIsRunning)
                {
                    CloseAppWithRunner();
                }

                Application.Top.Running = false;
                Application.RequestStop();
            };

            okButton.Clicked = () =>
            {
                var nodeAddressString = nodeAddressTextField.Text.ToString();

                if (string.IsNullOrWhiteSpace(nodeAddressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Node address is empty." +
                                                          $"{Environment.NewLine}(ESC to close)");
                    return;
                }

                if (!_urlRegex.IsMatch(nodeAddressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Node address is invalid." +
                                                          $"{Environment.NewLine}(ESC to close)");
                    return;
                }

                var addressString = addressTextField.Text.ToString();

                if (string.IsNullOrWhiteSpace(addressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Address is empty." +
                                                          $"{Environment.NewLine}(ESC to close)");
                    return;
                }

                if (addressString.Length != 42 || !_addressRegex.IsMatch(addressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Address is invalid." +
                                                          $"{Environment.NewLine}(ESC to close)");
                    return;
                }

                AddressesSelected?.Invoke(this, (nodeAddressString, addressString, _process, _externalRunnerIsRunning));
            };
            _mainWindow.Add(addressLabel, nodeAddressLabel, nodeAddressTextField,
                addressTextField, okButton, quitButton);
            return Task.FromResult(_mainWindow);
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
