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

            var window = new Window("Provider sessions") {X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill()};

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
                        X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill()
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
