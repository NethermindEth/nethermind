// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
using System.Linq;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Databases;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor.Modules.Data.Providers
{
    public class DepositsProvider : IDataProvider
    {
        public void Init(string path)
        {
            var dbOnTheRocks = new DepositsRocksDb(path, new DbConfig(), LimboLogs.Instance);
            var depositsBytes = dbOnTheRocks.GetAll();
            var depositsDecoder = new DepositDecoder();
            var deposits = depositsBytes
                .Select(b => depositsDecoder.Decode(b.Value.AsRlpStream()));

            var window = new Window("Deposits") { X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill() };
            if (!deposits.Any())
            {
                MessageBox.Query(40, 7, "Deposits", "No data." +
                                                    $"{Environment.NewLine}(ESC to close)");
                window.FocusPrev();
                return;
            }

            var y = 1;
            foreach (var deposit in deposits)
            {
                var depositBtn = new Button(1, y++, $"Units: {deposit.Units}, Value: {deposit.Value}");

                depositBtn.Clicked = () =>
                {
                    var depositDetailsWindow = new Window("Deposit details")
                    {
                        X = 0,
                        Y = 10,
                        Width = Dim.Fill(),
                        Height = Dim.Fill()
                    };
                    Application.Top.Add(depositDetailsWindow);
                    var serializer = new EthereumJsonSerializer();
                    var dataAssetLbl = new Label(1, 1, serializer.Serialize(deposit, true));

                    depositDetailsWindow.Add(dataAssetLbl);
                    Application.Run(depositDetailsWindow);
                };
            }

            Application.Top.Add(window);
            Application.Run(window);
        }
    }
}
