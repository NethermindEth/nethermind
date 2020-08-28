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
using Nethermind.BeamWallet.Modules.Events;
using Nethermind.BeamWallet.Modules.Init;
using Terminal.Gui;

namespace Nethermind.BeamWallet.Modules.Addresses
{
    internal class AddressesModule : IModule
    {
        private readonly Regex _urlRegex = new Regex(@"^http(s)?://([\w-]+.)+[\w-]+(/[\w- ./?%&=])?",
            RegexOptions.Compiled);

        private readonly Regex _addressRegex = new Regex("(0x)([0-9A-Fa-f]{40})", RegexOptions.Compiled);
        private readonly IJsonRpcWalletClientProxy _jsonRpcWalletClientProxy;
        private readonly Option _option;
        private const string DefaultUrl = "http://localhost:8545";
        private Window _window;
        private Button _okButton;
        private Button _backButton;
        public event EventHandler<AddressesSelectedEventArgs> AddressesSelected;

        public AddressesModule(Option option, IJsonRpcWalletClientProxy jsonRpcWalletClientProxy)
        {
            // if (!File.Exists(path))
            // {
            //     return;
            // }
            _option = option;
            _jsonRpcWalletClientProxy = jsonRpcWalletClientProxy;
            CreateWindow();
        }

        private void CreateWindow()
        {
            _window = new Window("Beam Wallet") {X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()};
        }

        public Task<Window> InitAsync() => _option switch
        {
            Option.ProvideAddress => HandleProvidedAddress(),
            Option.CreateNewAccount => HandleNewAccount(),
            _ => default
        };

        private Task<Window> HandleNewAccount()
        {
            var passphraseInfo = new Label(1, 1, "Do not lose your passphrase." +
                                                 $"{Environment.NewLine}{Environment.NewLine}" +
                                                 "We dont have an access to your " +
                                                 "passphrase so there is no chance of getting it back." +
                                                 $"{Environment.NewLine}{Environment.NewLine}" +
                                                 "Never give your passphrase to anyone. Your founds can be stolen." +
                                                 $"{Environment.NewLine}{Environment.NewLine}" +
                                                 "Set a strong passphrase. We recommend writing it down on a paper." +
                                                 $"{Environment.NewLine}{Environment.NewLine}" +
                                                 "If you lose your passphrase we will not be able to help you." +
                                                 $"{Environment.NewLine}{Environment.NewLine}" +
                                                 "Your whole money will be gone.");

            var passphraseLabel = new Label(1, 14, "Enter passphrase:");
            var passphraseTextField = new TextField(25, 14, 80, "");

            var confirmationPassphraseLabel = new Label(1, 16, "Confirm passphrase:");
            var confirmationPassphraseTextField = new TextField(25, 16, 80, "");

            passphraseTextField.Secret = true;
            confirmationPassphraseTextField.Secret = true;

            _okButton = new Button(35, 18, "OK");
            _backButton = new Button(25, 18, "Back");
            _backButton.Clicked = () =>
            {
                Back();
            };

            _okButton.Clicked = async () =>
            {
                var passphrase = passphraseTextField.Text.ToString();
                var confirmationPassphrase = confirmationPassphraseTextField.Text.ToString();

                if (string.IsNullOrWhiteSpace(passphrase))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Passphrase can not be empty." +
                                                          $"{Environment.NewLine}(ESC to close)");
                    return;
                }

                if (string.IsNullOrWhiteSpace(confirmationPassphrase))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Confirmation passphrase can not be empty." +
                                                          $"{Environment.NewLine}(ESC to close)");
                    return;
                }

                if (passphrase != confirmationPassphrase)
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Provided passphrases do not match." +
                                                          $"{Environment.NewLine}(ESC to close)");
                    return;
                }
                _window.Remove(_okButton);
                _window.SetFocus(_backButton);
                await CreateAccount(passphrase);
                _window.Add(_okButton);
            };
            _window.Add(passphraseInfo, passphraseLabel, passphraseTextField,
                confirmationPassphraseLabel, confirmationPassphraseTextField, _backButton, _okButton);
            return Task.FromResult(_window);
        }

        private async Task CreateAccount(string passphrase)
        {
            var address = await Extensions.TryExecuteAsync(() =>
                _jsonRpcWalletClientProxy.personal_newAccount(passphrase));
            AddressesSelected?.Invoke(this, new AddressesSelectedEventArgs(DefaultUrl, address.ToString()));
        }

        private Task<Window> HandleProvidedAddress()
        {
            var nodeAddressLabel = new Label(3, 22, "Enter node address:");
            var nodeAddressTextField = new TextField(28, 22, 80, $"{DefaultUrl}");

            var addressLabel = new Label(3, 1, "Enter account address:");
            var addressTextField = new TextField(28, 1, 80, "");

            _okButton = new Button(28, 3, "OK");
            var backButton = new Button(36, 3, "Back");
            backButton.Clicked = () =>
            {
                Back();
            };

            _okButton.Clicked = () =>
            {
                var nodeAddressString = nodeAddressTextField.Text.ToString();

                if (string.IsNullOrWhiteSpace(nodeAddressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Node address can not be empty." +
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
                    MessageBox.ErrorQuery(40, 7, "Error", "Address can not be empty." +
                                                          $"{Environment.NewLine}(ESC to close)");
                    return;
                }

                if (addressString.Length != 42 || !_addressRegex.IsMatch(addressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Address is invalid." +
                                                          $"{Environment.NewLine}(ESC to close)");
                    return;
                }

                AddressesSelected?.Invoke(this, new AddressesSelectedEventArgs(nodeAddressString, addressString));
            };
            _window.Add(addressLabel, addressTextField, _okButton, backButton);
            return Task.FromResult(_window);
        }

        private static void Back()
        {
            Application.Top.Running = false;
            Application.RequestStop();
            Application.Shutdown();
        }
    }
}
