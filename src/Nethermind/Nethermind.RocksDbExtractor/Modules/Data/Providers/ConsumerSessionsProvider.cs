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

            var window = new Window("Consumer sessions") {X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill()};
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
                        X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill()
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
