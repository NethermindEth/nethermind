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
using Nethermind.BeamWallet.Services;
using Terminal.Gui;

namespace Nethermind.BeamWallet.Modules.Network
{
    internal class NetworkModule : IModule
    {
        private const int PositionX = 1;
        private Window _window;
        private string _network;
        private readonly IRunnerValidator _runnerValidator;
        public event EventHandler<string> NetworkSelected;

        public NetworkModule(IRunnerValidator runnerValidator)
        {
            _runnerValidator = runnerValidator;
        }

        public Task<Window> InitAsync()
        {
            CheckRunnerStatusAsync();
            CreateWindow();
            InitNetworks();

            return Task.FromResult(_window);
        }

        private async Task CheckRunnerStatusAsync()
        {
            var runnerRunning = await _runnerValidator.IsRunningAsync();
            if (runnerRunning)
            {
                NetworkSelected?.Invoke(this, string.Empty);
            }
        }

        private void CreateWindow()
        {
            _window = new Window("Beam Wallet") {X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()};
        }

        private void InitNetworks()
        {
            var mainnetButton = new Button(PositionX, 1, "Mainnet");
            var goerliButton = new Button(PositionX, 2, "Goerli");
            var quitButton = new Button(PositionX, 4, "Quit");
            mainnetButton.Clicked = () =>
            {
                _network = "mainnet";
                NetworkSelected?.Invoke(this, _network);
            };
            goerliButton.Clicked = () =>
            {
                _network = "goerli";
                NetworkSelected?.Invoke(this, _network);
            };
            quitButton.Clicked = () =>
            {
                Quit();
            };
            _window.Add(mainnetButton, goerliButton, quitButton);
        }

        private void Quit()
        {
            Application.Top.Running = false;
            Application.RequestStop();
            Application.Shutdown();
        }
    }
}
