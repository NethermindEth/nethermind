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
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeamWallet.Services;
using Nethermind.Facade.Proxy;
using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace Nethermind.BeamWallet.Modules.Init
{
    internal class InitModule : IModule
    {
        private const string FileName = "Nethermind.Runner";
        private const int PositionX = 1;
        private Process _process;
        private Timer _timer;
        private Window _window;
        private Label _runnerOnInfo;
        private Label _runnerOffInfo;
        private bool _backgroundRunnerIsRunning;
        private int _processId;
        private readonly IEthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private readonly IRunnerValidator _runnerValidator;
        private readonly string _network;

        private static readonly Dictionary<string, string> _networks = new Dictionary<string, string>
        {
            ["1"] = "Mainnet",
            ["3"] = "Ropsten",
            ["4"] = "Rinkeby",
            ["5"] = "Goerli",
            ["99"] = "Unknown",
        };

        public event EventHandler<Option> OptionSelected;

        public InitModule(IEthJsonRpcClientProxy ethJsonRpcClientProxy, IRunnerValidator runnerValidator,
            string network)
        {
            _ethJsonRpcClientProxy = ethJsonRpcClientProxy;
            _runnerValidator = runnerValidator;
            _network = network;
        }

        public Task<Window> InitAsync()
        {
            CreateWindow();
            CreateProcess();
            StartProcessAsync();
            InitOptions();
            return Task.FromResult(_window);
        }

        private void CreateWindow()
        {
            _window = new Window("Beam Wallet") {X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()};
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
            AddInitInfo();
            AddRunnerInfo("Launching Nethermind...");

            var runnerIsRunning = await _runnerValidator.IsRunningAsync();
            if (runnerIsRunning)
            {
                _backgroundRunnerIsRunning = false;
                AddRunnerInfo("Nethermind is already running.");
                await SetNetwork();
                return;
            }    
            _timer = new Timer(Update, null, TimeSpan.Zero, TimeSpan.FromSeconds(8));

            try
            {
                _process.Start();
                _processId = _process.Id;
                _backgroundRunnerIsRunning = true;
            }
            catch
            {
                AddRunnerInfo("Error with starting a Nethermind node.");
                _backgroundRunnerIsRunning = false;
            }
        }

        private void AddInitInfo()
        {
            var beamWalletInfo = new Label(PositionX, 1,
                "Hello, Welcome to Nethermind Beam Wallet! Beam Wallet is a console " +
                $"{Environment.NewLine}" +
                "application which allows to check account balances and make simple transactions." +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "Already have an account?" +
                $"{Environment.NewLine}" +
                "You will need: your address, passphrase and your keystore file," +
                $"{Environment.NewLine}" +
                "Before we start, please copy your keystore file into 'keystore' folder." +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "Don't have an account? " +
                $"{Environment.NewLine}" +
                "Create one using \"Create new account\" button." +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "To navigate through the application - use the TAB key or Up and Down arrows.");

            var betaVersionWarningInfo = new Label(PositionX, 13,
                "This is a Beta version, so for your own safety please, do " +
                "not use an account with a high balance.");

            var warningInfo = new Label(PositionX, 15, "There are a few things that can go wrong:" +
                                                       $"{Environment.NewLine}" +
                                                       "your balance may be incorrect and the transaction fee may be charged incorrectly");

            betaVersionWarningInfo.TextColor = new Attribute(Color.White, Color.BrightRed);

            _window.Add(betaVersionWarningInfo, warningInfo, beamWalletInfo);
        }

        private void AddRunnerInfo(string info)
        {
            if (_runnerOnInfo is {})
            {
                _window.Remove(_runnerOnInfo);
            }

            _runnerOnInfo = new Label(PositionX, 18, $"{info}");
            _window.Add(_runnerOnInfo);
        }

        private async Task SetNetwork()
        {
            RpcResult<string> netVersionResult;
            do
            {
                netVersionResult = await _ethJsonRpcClientProxy.net_version();
                if (!netVersionResult.IsValid)
                {
                    await Task.Delay(2000);
                }
            } while (!netVersionResult.IsValid);

            var network = _networks.TryGetValue(netVersionResult.Result, out var foundNetwork)
                ? foundNetwork
                : "unknown";
            AddNetworkInfo(network);
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
                    AddNetworkInfo(_network);
                }

                return;
            }
            catch
            {
                // ignored
            }

            if (process is null)
            {
                RecreateProcess();
            }

            if (_runnerOffInfo is {})
            {
                _window.Remove(_runnerOffInfo);
            }

            _runnerOnInfo = new Label(PositionX, 18, "Nethermind is running.");
            _window.Add(_runnerOnInfo);
        }

        private void RecreateProcess()
        {
            if (_runnerOnInfo is {})
            {
                _window.Remove(_runnerOnInfo);
            }

            _runnerOffInfo = new Label(PositionX, 18, "Nethermind node is stopped.. Please, wait for it to start.");
            _window.Add(_runnerOffInfo);
            _process.Start();
            _processId = _process.Id;
        }

        private void AddNetworkInfo(string info)
        {
            var networkInfo = new Label(PositionX, 19, $"Network: {info}");
            _window.Add(networkInfo);
        }

        private void InitOptions()
        {
            var createAccountButton = new Button(PositionX, 21, "Create new account");
            createAccountButton.Clicked = () =>
            {
                OptionSelected?.Invoke(this, Option.CreateNewAccount);
            };
            var provideAccountButton = new Button(25, 21, "Provide an address");
            provideAccountButton.Clicked = () =>
            {
                OptionSelected?.Invoke(this, Option.ProvideAddress);
            };
            var quitButton = new Button(PositionX, 22, "Quit");
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
                catch (Exception ex)
                {
                    MessageBox.ErrorQuery(50, 7, "Error", "There was an error while " +
                                                          "closing Nethermind. (ESC to close)");
                }
            }
        }
    }
}
