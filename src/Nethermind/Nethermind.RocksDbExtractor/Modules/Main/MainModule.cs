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
using System.Linq;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor.Modules.Main
{
    internal class MainModule : IModule
    {
        public event EventHandler<string> PathSelected;
        
        public Window Init()
        {
            AddDecoders();
            var window = new Window ("NDM RocksDb Extractor")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = 10
            };
            var pathLbl = new Label(3, 3, "Enter DB path: ");
            var pathTxtField = new TextField(20, 3, 70, "");
            var okBtn = new Button(90, 3, "OK");
            var quitBtn = new Button(3, 1, "Quit");
            quitBtn.Clicked = () =>
            {
                Application.Top.Running = false;
                Application.RequestStop();
            };

            okBtn.Clicked = () =>
            {
                var pathString = pathTxtField.Text.ToString();
                if (double.TryParse(pathString, out _))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Path can not be a number.");
                    pathTxtField.Text = string.Empty;
                    return;
                }

                if (string.IsNullOrEmpty(pathString))
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Path can not be empty.");
                    return;
                }

                if (!System.IO.Directory.GetDirectories(pathString, "*").Any())
                {
                    MessageBox.ErrorQuery(40, 7, "Error", "Directory is empty.");
                    return;
                }

                PathSelected?.Invoke(this, pathString);
            };
            
            window.Add(quitBtn, pathLbl, pathTxtField, okBtn);

            return window;
        }
        
        private static void AddDecoders()
        {
            DataDeliveryReceiptDecoder.Init();
            DataDeliveryReceiptRequestDecoder.Init();
            DataDeliveryReceiptToMergeDecoder.Init();
            DataDeliveryReceiptDetailsDecoder.Init();
            DataAssetDecoder.Init();
            DataAssetRuleDecoder.Init();
            DataAssetRulesDecoder.Init();
            DataAssetProviderDecoder.Init();
            DataRequestDecoder.Init();
            DepositDecoder.Init();
            DepositApprovalDecoder.Init();
            EarlyRefundTicketDecoder.Init();
            EthRequestDecoder.Init();
            FaucetResponseDecoder.Init();
            FaucetRequestDetailsDecoder.Init();
            SessionDecoder.Init();
            TransactionInfoDecoder.Init();
            UnitsRangeDecoder.Init();
        }
    }
}