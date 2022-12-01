// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
using System.Linq;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.RocksDbExtractor.ProviderDecoders.RocksDb;
using Nethermind.Serialization.Json;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor.Modules.Data.Providers
{
    public class ProviderReceiptsProvider : IDataProvider
    {
        public void Init(string path)
        {
            var dbOnTheRocks = new ProviderReceiptsRocksDb(path, new DbConfig(), LimboLogs.Instance);
            var receiptsBytes = dbOnTheRocks.GetAll();
            var receiptDecoder = new DataDeliveryReceiptDetailsDecoder();
            var receipts = receiptsBytes
                .Select(b => receiptDecoder.Decode(b.Value.AsRlpStream()));

            var window = new Window("Provider receipts") { X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill() };
            if (!receipts.Any())
            {
                MessageBox.Query(40, 7, "Provider receipts", "No data." +
                                                             $"{Environment.NewLine}(ESC to close)");
                window.FocusPrev();
                return;
            }

            var y = 1;
            foreach (var receipt in receipts)
            {
                var receiptBtn = new Button(1, y++, $"Id: {receipt.Id}, DepositId: {receipt.DepositId}");

                receiptBtn.Clicked = () =>
                {
                    var receiptDetailsWindow = new Window("Receipt details")
                    {
                        X = 0,
                        Y = 10,
                        Width = Dim.Fill(),
                        Height = Dim.Fill()
                    };
                    Application.Top.Add(receiptDetailsWindow);
                    var serializer = new EthereumJsonSerializer();
                    var receiptLbl = new Label(1, 1, serializer.Serialize(receipt, true));
                    receiptDetailsWindow.Add(receiptLbl);
                    Application.Run(receiptDetailsWindow);
                };
                window.Add(receiptBtn);
            }

            Application.Top.Add(window);
            Application.Run(window);
        }
    }
}
