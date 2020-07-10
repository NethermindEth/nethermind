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
using System.Threading.Tasks;
using Terminal.Gui;

namespace Nethermind.BeamWallet.Modules.Addresses
{
    internal class AddressesModule : IModule
    {
        private static readonly Regex _urlRegex = new Regex(@"^http(s)?://([\w-]+.)+[\w-]+(/[\w- ./?%&=])?",
            RegexOptions.Compiled);
        private static readonly Regex _addressRegex = new Regex("(0x)([0-9A-Fa-f]{40})", RegexOptions.Compiled);
        public event EventHandler<(string nodeAddress, string address)> AddressesSelected;

        public Task<Window> InitAsync()
        {
            var mainWindow = new Window("Beam Wallet")
            {
                X = 0, 
                Y = 0, 
                Width = Dim.Fill(), 
                Height = 10
            };
            var nodeAddressLabel = new Label(3, 1, "Enter node address:");
            var nodeAddressTextField = new TextField(28, 1, 80, "");
            var addressLabel = new Label(3, 3, "Enter account address:");
            var addressTextField = new TextField(28, 3, 80, "");
            
            var okButton = new Button(28, 5, "OK");
            var quitButton = new Button(36, 5, "Quit");
            quitButton.Clicked = () =>
            {
                Application.Top.Running = false;
                Application.RequestStop();
            };
            okButton.Clicked = () =>
            {
                var nodeAddressString = nodeAddressTextField.Text.ToString();
                
                if (string.IsNullOrWhiteSpace(nodeAddressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Node address is empty.");
                    return;
                }

                if (!_urlRegex.IsMatch(nodeAddressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Node address is invalid.");
                    return;
                }
                
                var addressString = addressTextField.Text.ToString();
                
                if (string.IsNullOrWhiteSpace(addressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Address is empty.");
                    return;
                }

                if (!_addressRegex.IsMatch(addressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Address is invalid.");
                    return;
                }
                
                AddressesSelected?.Invoke(this, (nodeAddressString, addressString));
            };
            mainWindow.Add(quitButton, nodeAddressLabel, nodeAddressTextField, addressLabel,
                addressTextField, okButton);

            return Task.FromResult(mainWindow);
        }
    }
}
