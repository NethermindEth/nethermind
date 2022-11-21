// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
using System.Linq;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Databases;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor.Modules.Data.Providers
{
    public class ConsumerSessionsProvider : IDataProvider
    {
        public void Init(string path)
        {
            var dbOnTheRocks = new ConsumerSessionsRocksDb(path, new DbConfig(), LimboLogs.Instance);
            var consumerSessionsBytes = dbOnTheRocks.GetAll();
            var consumerSessionsDecoder = new ConsumerSessionDecoder();
            var consumerSessions = consumerSessionsBytes
                .Select(b => consumerSessionsDecoder.Decode(b.Value.AsRlpStream()));

            var window = new Window("Consumer sessions") { X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill() };
            if (!consumerSessions.Any())
            {
                MessageBox.Query(40, 7, "Consumer sessions", "No data." +
                                                             $"{Environment.NewLine}(ESC to close)");
                window.FocusPrev();
                return;
            }

            var y = 1;
            foreach (var consumerSession in consumerSessions)
            {
                var consumerSessionBtn = new Button(1, y++, $"DepositId: {consumerSession.DepositId}," +
                                                            $"ConsumerAddress: {consumerSession.ConsumerAddress}");

                consumerSessionBtn.Clicked = () =>
                {
                    var consumerSessionDetailsWindow = new Window("Session details")
                    {
                        X = 0,
                        Y = 10,
                        Width = Dim.Fill(),
                        Height = Dim.Fill()
                    };
                    Application.Top.Add(consumerSessionDetailsWindow);
                    var serializer = new EthereumJsonSerializer();
                    var consumerSessionLbl = new Label(1, 1, serializer.Serialize(consumerSession, true));
                    consumerSessionDetailsWindow.Add(consumerSessionLbl);
                    Application.Run(consumerSessionDetailsWindow);
                };
                window.Add(consumerSessionBtn);
            }

            Application.Top.Add(window);
            Application.Run(window);
        }
    }
}
