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
    public class DataAssetsProvider : IDataProvider
    {
        public void Init(string path)
        {
            var dbOnTheRocks = new DataAssetsRocksDb(path, new DbConfig(), LimboLogs.Instance);
            var dataAssetsBytes = dbOnTheRocks.GetAll();
            var dataAssetDecoder = new DataAssetDecoder();
            var dataAssets = dataAssetsBytes
                .Select(b => dataAssetDecoder.Decode(b.Value.AsRlpStream()));

            var window = new Window("Data assets") { X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill() };
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
                        X = 0,
                        Y = 10,
                        Width = Dim.Fill(),
                        Height = Dim.Fill()
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
