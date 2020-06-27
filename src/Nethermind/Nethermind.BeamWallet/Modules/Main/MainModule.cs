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
using Terminal.Gui;

namespace Nethermind.BeamWallet.Modules.Main
{
    internal class MainModule : IModule
    {
        public event EventHandler<string> AddressSelected;

        public Task<Window> InitAsync()
        {
            var mainWindow = new Window("Beam Wallet")
            {
                X = 0, 
                Y = 0, 
                Width = Dim.Fill(), 
                Height = 10
            };
            var addressLbl = new Label(3, 3, "Enter address:");
            var addressTxtField = new TextField(20, 3, 80, "");
            var okBtn = new Button(100, 3, "OK");
            var quitBtn = new Button(3, 1, "Quit");
            quitBtn.Clicked = () =>
            {
                Application.Top.Running = false;
                Application.RequestStop();
            };
            okBtn.Clicked = () =>
            {
                var addressString = addressTxtField.Text.ToString();
                if (double.TryParse(addressString, out _))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Address can not be a number.");
                    addressTxtField.Text = string.Empty;
                    return;
                }

                if (string.IsNullOrEmpty(addressString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Address can not be empty.");
                }
                
                AddressSelected?.Invoke(this, addressString);
            };
            mainWindow.Add(quitBtn, addressLbl, addressTxtField, okBtn);

            return Task.FromResult(mainWindow);
        }
    }
}
