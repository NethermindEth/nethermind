// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
using System.Linq;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.RocksDbExtractor.ProviderDecoders;
using Nethermind.RocksDbExtractor.ProviderDecoders.RocksDb;
using Nethermind.Serialization.Json;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor.Modules.Data.Providers
{
    public class ConsumersProvider : IDataProvider
    {
        public void Init(string path)
        {
            var dbOnTheRocks = new ConsumersRocksDb(path, new DbConfig(), LimboLogs.Instance);
            var consumerBytes = dbOnTheRocks.GetAll();
            var consumerDecoder = new ConsumerDecoder();
            var consumers = consumerBytes
                .Select(b => consumerDecoder.Decode(b.Value.AsRlpStream()));

            var window = new Window("Consumers") { X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill() };
            if (!consumers.Any())
            {
                MessageBox.Query(40, 7, "Consumers", "No data." +
                                                     $"{Environment.NewLine}(ESC to close)");
                window.FocusPrev();
                return;
            }

            var y = 1;
            foreach (var consumer in consumers)
            {
                var consumerBtn = new Button(1, y++, $"DepositId: {consumer.DepositId}");

                consumerBtn.Clicked = () =>
                {
                    var consumerDetailsWindow = new Window("Consumer details")
                    {
                        X = 0,
                        Y = 10,
                        Width = Dim.Fill(),
                        Height = Dim.Fill()
                    };
                    Application.Top.Add(consumerDetailsWindow);
                    var serializer = new EthereumJsonSerializer();
                    var consumerLbl = new Label(1, 1, serializer.Serialize(consumer, true));
                    consumerDetailsWindow.Add(consumerLbl);
                    Application.Run(consumerDetailsWindow);
                };
                window.Add(consumerBtn);
            }

            Application.Top.Add(window);
            Application.Run(window);
        }
    }
}
