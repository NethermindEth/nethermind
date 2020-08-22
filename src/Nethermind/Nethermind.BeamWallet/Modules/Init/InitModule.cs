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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace Nethermind.BeamWallet.Modules.Init
{
    internal class InitModule : IModule
    {
        private const string DefaultUrl = "http://localhost:8545";
        private const string FileName = "Nethermind.Runner";
        private Process _process;
        private Timer _timer;
        private Window _window;
        private Label _runnerOnInfo;
        private Label _runnerOffInfo;
        private EthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private ProcessInfo _processInfo;
        private bool _backgroundRunnerIsRunning;
        private int _processId;
        private int x = 3;
        private string _network;

        public event EventHandler<Option> OptionSelected;

        public InitModule(string network)
        {
            // if (!File.Exists(path))
            // {
            //     return;
            // }
            _network = network;
        }

        private void InitData()
        {
            var httpClient = new HttpClient();
            var urls = new[] {DefaultUrl};
            var jsonRpcClientProxy = new JsonRpcClientProxy(new DefaultHttpClient(httpClient,
                new EthereumJsonSerializer(), LimboLogs.Instance, 0), urls, LimboLogs.Instance);
            _ethJsonRpcClientProxy = new EthJsonRpcClientProxy(jsonRpcClientProxy);
        }
        
        public Task<Window> InitAsync()
        {
            InitData();
            CreateWindow();
            CreateProcess();
            StartProcessAsync();
            InitOptions();
            return Task.FromResult(_window);
        }

        private void CreateWindow()
        {
            _window = new Window("Beam Wallet")
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
                    Arguments = $"--config {_network}_beam --JsonRpc.Enabled true",
                    RedirectStandardOutput = true
                }
            };
        }

        private static string GetFileName()
            => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{FileName}.exe" : $"./{FileName}";

        private async Task StartProcessAsync()
        {
            AddInfo();
            AddRunnerInfo("Launching Nethermind...");

            var runnerIsRunning = await CheckIsProcessRunningAsync();
            if (runnerIsRunning)
            {
                _backgroundRunnerIsRunning = false;
                AddRunnerInfo("Nethermind is already running.");
                return;
            }

            try
            {
                _process.Start();
                _processId = _process.Id;
                _timer = new Timer(Update, null, TimeSpan.Zero, TimeSpan.FromSeconds(8));
                _backgroundRunnerIsRunning = true;
            }
            catch
            {
                AddRunnerInfo("Error with starting a Nethermind node.");
                _backgroundRunnerIsRunning = false;
            }
        }

        private async Task<bool> CheckIsProcessRunningAsync()
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
                AddRunnerInfo("Nethermind node is running.");
                if (!string.IsNullOrEmpty(_network))
                {
                    AddNetworkInfo($"Network: {_network}");
                }
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
                    _window.Remove(_runnerOnInfo);
                }

                _runnerOffInfo = new Label(x, 26, "Nethermind node is stopped.. Please, wait for it to start.");
                _window.Add(_runnerOffInfo);
                _process.Start();
                _processId = _process.Id;
            }

            if (_runnerOffInfo is {})
            {
                _window.Remove(_runnerOffInfo);
            }

            _runnerOnInfo = new Label(x, 26, "Nethermind is running.");
            _window.Add(_runnerOnInfo);
        }

        private void AddNetworkInfo(string info)
        {
            var networkInfo = new Label(x, 27, $"{info}");
            _window.Add(networkInfo);
        }

        private void AddInfo()
        {
            var beamWalletInfo = new Label(x, 1, "Hello, Welcome to Nethermind Beam Wallet - a simple " +
                                                 $"{Environment.NewLine}" +
                                                 "console application that allows you to use the power of beam sync." +
                                                 $"{Environment.NewLine}" +
                                                 "Beam Wallet is running without external dependencies (automatically launches " +
                                                 $"{Environment.NewLine}" +
                                                 "a Nethermind Node in the background) and allows to check account balances " +
                                                 $"{Environment.NewLine}" +
                                                 "and make simple transactions on mainnet." +
                                                 $"{Environment.NewLine}{Environment.NewLine}" +
                                                 "Already have an account?" +
                                                 $"{Environment.NewLine}" +
                                                 "If you already have an account, you can use it - in that case you will need:" +
                                                 $"{Environment.NewLine}" +
                                                 "- your address" +
                                                 $"{Environment.NewLine}" +
                                                 "- your passphrase" +
                                                 $"{Environment.NewLine}" +
                                                 "- your keystore file" +
                                                 $"{Environment.NewLine}" +
                                                 "Before we start, please copy keystore file of your account into " +
                                                 "folder 'keystore' - this is" +
                                                 $"{Environment.NewLine}" +
                                                 "necessary to properly unlock the account before making a transaction." +
                                                 $"{Environment.NewLine}{Environment.NewLine}" +
                                                 "Don't have an account? " +
                                                 $"{Environment.NewLine}" +
                                                 "Create one using \"Create new account\" button." +
                                                 $"{Environment.NewLine}{Environment.NewLine}" +
                                                 "To navigate through the application - use the TAB key or Up and Down arrows.");
            
            var betaVersionWarningInfo = new Label(x, 20, "This is a Beta version, so for your own safety please, do " +
                                                         "not use an account with a high balance.");

            var warningInfo = new Label(x, 22, "There are a few things that can go wrong:" +
                                              $"{Environment.NewLine}" +
                                              "- your balance may be incorrect" +
                                              $"{Environment.NewLine}" +
                                              "- the transaction fee may be charged incorrectly");
            
            betaVersionWarningInfo.TextColor = new Attribute(Color.White, Color.BrightRed);
            
            _window.Add(betaVersionWarningInfo, warningInfo, beamWalletInfo);
        }

        private void AddRunnerInfo(string info)
        {
            if (_runnerOnInfo is {})
            {
                _window.Remove(_runnerOnInfo);
            }

            _runnerOnInfo = new Label(x, 26, $"{info}");
            _window.Add(_runnerOnInfo);
        }

        private void InitOptions()
        {
            var createAccountButton = new Button(x, 29, "Create new account");
            createAccountButton.Clicked = () =>
            {
                OptionSelected?.Invoke(this, Option.CreateNewAccount);
            };
            var provideAccountButton = new Button(27, 29, "Provide an address");
            provideAccountButton.Clicked = () =>
            {
                OptionSelected?.Invoke(this, Option.ProvideAddress);
            };
            var quitButton = new Button(x, 31, "Quit");
            quitButton.Clicked = () =>
            {
                Quit();
            };
            _window.Add(createAccountButton, provideAccountButton, quitButton);
        }

        private void Quit()
        {
            if (_backgroundRunnerIsRunning)
            {
                CloseAppWithRunner();
            }
            Application.Top.Running = false;
            Application.RequestStop();
            Application.Shutdown();
        }

        private void CloseAppWithRunner()
        {
            var confirmed = MessageBox.Query(80, 8, "Confirmation",
                $"{Environment.NewLine}" +
                "Nethermind node is running in the background. Do you want to stop it?", "Yes", "No");

            if (confirmed == 0)
            {
                try
                {
                    _process.Kill();
                }
                catch(Exception ex)
                {
                    MessageBox.ErrorQuery(50, 7, "Error", "There was an error while " +
                                                          "closing Nethermind. (ESC to close)");
                }
            }
        }
    }
}
