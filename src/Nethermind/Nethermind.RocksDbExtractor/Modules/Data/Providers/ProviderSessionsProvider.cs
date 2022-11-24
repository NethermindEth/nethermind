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
    public class ProviderSessionsProvider : IDataProvider
    {
        public void Init(string path)
        {
            var dbOnTheRocks = new ProviderSessionsRocksDb(path, new DbConfig(), LimboLogs.Instance);
            var providerSessionsBytes = dbOnTheRocks.GetAll();
            var providerSessionsDecoder = new ProviderSessionDecoder();
            var providerSessions = providerSessionsBytes
                .Select(b => providerSessionsDecoder.Decode(b.Value.AsRlpStream()));

            var window = new Window("Provider sessions") { X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill() };

            if (!providerSessions.Any())
            {
                MessageBox.Query(40, 7, "Provider sessions", "No data." +
                                                             $"{Environment.NewLine}(ESC to close)");
                window.FocusPrev();
                return;
            }

            var y = 1;
            foreach (var providerSession in providerSessions)
            {
                var providerSessionBtn = new Button(1, y++, $"DepositId: {providerSession.DepositId}," +
                                                            $"ProviderAddress: {providerSession.ProviderAddress}");

                providerSessionBtn.Clicked = () =>
                {
                    var providerSessionDetailsWindow = new Window("Session details")
                    {
                        X = 0,
                        Y = 10,
                        Width = Dim.Fill(),
                        Height = Dim.Fill()
                    };
                    Application.Top.Add(providerSessionDetailsWindow);
                    var serializer = new EthereumJsonSerializer();
                    var providerSessionLbl = new Label(1, 1, serializer.Serialize(providerSession, true));
                    providerSessionDetailsWindow.Add(providerSessionLbl);
                    Application.Run(providerSessionDetailsWindow);
                };
                window.Add(providerSessionBtn);
            }

            Application.Top.Add(window);
            Application.Run(window);
        }
    }
}
