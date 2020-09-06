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

            var window = new Window("Deposits") {X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill()};
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
                        X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill()
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
