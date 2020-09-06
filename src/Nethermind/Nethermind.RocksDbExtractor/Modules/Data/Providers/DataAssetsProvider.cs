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
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.RocksDbExtractor.ProviderDecoders.RocksDb;
using Nethermind.Serialization.Json;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor.Modules.Data.Providers
{
    public class DataAssetsProvider : IDataProvider
    {
        public void Init(string path)
        {
            var dbOnTheRocks = new DataAssetsRocksDb(path, new DbConfig(), LimboLogs.Instance);
            var dataAssetsBytes = dbOnTheRocks.GetAll();
            var dataAssetDecoder = new DataAssetDecoder();
            var dataAssets = dataAssetsBytes
                .Select(b => dataAssetDecoder.Decode(b.Value.AsRlpStream()));

            var window = new Window("Data assets") {X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill()};
            if (!dataAssets.Any())
            {
                MessageBox.Query(40, 7, "Data assets", "No data." +
                                                       $"{Environment.NewLine}(ESC to close)");
                window.FocusPrev();
                return;
            }

            var y = 1;
            foreach (var dataAsset in dataAssets)
            {
                var dataAssetsBtn = new Button(1, y++, $"Name: {dataAsset.Name}");

                dataAssetsBtn.Clicked = () =>
                {
                    var dataAssetDetailsWindow = new Window("Data asset details")
                    {
                        X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill()
                    };
                    Application.Top.Add(dataAssetDetailsWindow);

                    var serializer = new EthereumJsonSerializer();
                    var dataAssetLbl = new Label(1, 1, serializer.Serialize(dataAsset, true));

                    dataAssetDetailsWindow.Add(dataAssetLbl);
                    Application.Run(dataAssetDetailsWindow);
                };
                window.Add(dataAssetsBtn);
            }

            Application.Top.Add(window);
            Application.Run(window);
        }
    }
}
