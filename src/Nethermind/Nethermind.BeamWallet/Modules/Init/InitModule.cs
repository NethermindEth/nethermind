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
        private Process _process;
        private Timer _timer;
        private Window _mainWindow;
        private int _processId;
        private Label _runnerOnInfo;
        private Label _runnerOffInfo;
        private EthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private bool _externalRunnerIsRunning;
        private ProcessInfo _processInfo;
        private const string DefaultUrl = "http://localhost:8545";
        private const string FileName = "Nethermind.Runner";
        private int x = 3;

        public event EventHandler<(Option, ProcessInfo)> OptionSelected;

        public InitModule()
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
            AddRunnerInfo("Launching Nethermind...");

            var runnerIsRunning = await CheckIsProcessRunning();
            if (runnerIsRunning)
            {
                _externalRunnerIsRunning = true;
                AddRunnerInfo("Nethermind is already running.");
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
                AddRunnerInfo("Error with starting a Nethermind process.");
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
                AddRunnerInfo("Nethermind is running.");
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

                _runnerOffInfo = new Label(x, 26, "Nethermind is stopped.. Please, wait for it to start.");
                _mainWindow.Add(_runnerOffInfo);
                _process.Start();
                _processId = _process.Id;
            }

            if (_runnerOffInfo is {})
            {
                _mainWindow.Remove(_runnerOffInfo);
            }

            _runnerOnInfo = new Label(x, 26, "Nethermind is running.");
            _mainWindow.Add(_runnerOnInfo);
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
            
            _mainWindow.Add(betaVersionWarningInfo, warningInfo, beamWalletInfo);
        }

        private void AddRunnerInfo(string info)
        {
            if (_runnerOnInfo is {})
            {
                _mainWindow.Remove(_runnerOnInfo);
            }

            _runnerOnInfo = new Label(x, 26, $"{info}");
            _mainWindow.Add(_runnerOnInfo);
        }

        public Task<Window> InitAsync()
        {
            _processInfo = new ProcessInfo(_process, _externalRunnerIsRunning);
            
            var createAccountButton = new Button(x, 29, "Create new account");
            createAccountButton.Clicked = () =>
            {
                OptionSelected?.Invoke(this, (Option.CreateNewAccount, _processInfo));
            };
            var provideAccountButton = new Button(27, 29, "Provide an address");
            provideAccountButton.Clicked = () =>
            {
                OptionSelected?.Invoke(this, (Option.ProvideAddress, _processInfo));
            };
            var quitButton = new Button(x, 31, "Quit");
            quitButton.Clicked = () =>
            {
                if (!_externalRunnerIsRunning)
                {
                    CloseAppWithRunner();
                }
                Application.Top.Running = false;
                Application.RequestStop();
                Application.Shutdown();
            };
            _mainWindow.Add(createAccountButton, provideAccountButton, quitButton);
            return Task.FromResult(_mainWindow);
        }

        private void CloseAppWithRunner()
        {
            var confirmed = MessageBox.Query(80, 8, "Confirmation",
                $"{Environment.NewLine}" +
                "Nethermind is running in the background. Do you want to stop it?", "Yes", "No");

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
