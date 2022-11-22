// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
using System.Linq;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Terminal.Gui;
using DataDeliveryReceiptDecoder = Nethermind.RocksDbExtractor.ProviderDecoders.DataDeliveryReceiptDecoder;

namespace Nethermind.RocksDbExtractor.Modules.Main
{
    internal class MainModule : IModule
    {
        public event EventHandler<string> PathSelected;

        public Window Init()
        {
            AddDecoders();
            var window = new Window("NDM RocksDb Extractor") { X = 0, Y = 0, Width = Dim.Fill(), Height = 10 };
            var examplePathLabel = new Label(3, 3, "Example path: \"Users/Desktop/nethermind_db/ndm_consumer/local\"");
            var pathLbl = new Label(3, 5, "Enter DB path: ");
            var pathTxtField = new TextField(20, 5, 70, "");
            var okBtn = new Button(90, 5, "OK");
            var quitBtn = new Button(3, 1, "Quit");
            var backLabel = new Label(15, 1, "(Back: press \"q\" button)");
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

                try
                {
                    if (!System.IO.Directory.GetDirectories(pathString, "*").Any())
                    {
                        MessageBox.ErrorQuery(40, 7, "Error", "Directory is empty.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.ErrorQuery(50, 7, "Error", "There was an error while getting a path.");
                    return;
                }

                PathSelected?.Invoke(this, pathString);
            };

            window.Add(examplePathLabel, quitBtn, backLabel, pathLbl, pathTxtField, okBtn);

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
