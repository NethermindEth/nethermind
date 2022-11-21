// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.RocksDbExtractor.Modules.Data.Providers;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor.Modules.Data
{
    internal class DataModule : IModule
    {
        private static readonly IDictionary<string, Func<IDataProvider>> Providers =
            new Dictionary<string, Func<IDataProvider>>
            {
                ["blocks"] = () => new BlocksDataProvider(),
                ["dataAssets"] = () => new DataAssetsProvider(),
                ["providerReceipts"] = () => new ProviderReceiptsProvider(),
                ["consumerReceipts"] = () => new ConsumerReceiptsProvider(),
                ["providerSessions"] = () => new ProviderSessionsProvider(),
                ["consumerSessions"] = () => new ConsumerSessionsProvider(),
                ["providerDepositApprovals"] = () => new ProviderDepositApprovalsProvider(),
                ["consumerDepositApprovals"] = () => new ConsumerDepositApprovalsProvider(),
                ["paymentClaims"] = () => new PaymentClaimsProvider(),
                ["deposits"] = () => new DepositsProvider(),
                ["consumers"] = () => new ConsumersProvider()
            };

        private readonly string _path;

        public DataModule(string path)
        {
            _path = path;
        }

        public Window Init()
        {
            var mainWindow = new Window("") { X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill() };
            Application.Top.Add(mainWindow);

            var dataPaths = Directory.GetDirectories(_path, "*");

            var i = 1;
            var dataFolderButtons = new List<Button>();
            foreach (var dataPath in dataPaths)
            {
                var dataFolder = dataPath.Split(Path.DirectorySeparatorChar).Last();
                var dataFolderBtn = new Button(1, i++, $"{dataFolder}");
                dataFolderButtons.Add(dataFolderBtn);
                mainWindow.Add(dataFolderBtn);
                dataFolderBtn.Clicked = () =>
                {
                    if (Directory.GetDirectories(dataPath, "*").Any())
                    {
                        foreach (var dataFolderBtn in dataFolderButtons)
                        {
                            mainWindow.Remove(dataFolderBtn);
                        }

                        var innerDataPaths = Directory.GetDirectories(dataPath, "*");
                        var i = 1;
                        foreach (var innerDataPath in innerDataPaths)
                        {
                            var innerDataFolder = innerDataPath.Split(Path.DirectorySeparatorChar).Last();
                            var innerDataFolderBtn = new Button(1, i++, $"{innerDataFolder}");
                            mainWindow.Add(innerDataFolderBtn);
                            innerDataFolderBtn.Clicked = () =>
                            {
                                if (!Providers.TryGetValue(innerDataFolder, out var dataProviderFactory))
                                {
                                    MessageBox.ErrorQuery(40, 7, "Error", "Data provider not found");
                                    return;
                                }

                                var i = innerDataPath;
                                if (!Directory.GetDirectories(i, "*").Any())
                                {
                                    try
                                    {
                                        dataProviderFactory().Init(dataPath);
                                    }
                                    catch (Exception ex)
                                    {
                                        MessageBox.Query(50, 20, "Error", "There was an error with getting data." +
                                                                          $"{Environment.NewLine}{Environment.NewLine}" +
                                                                          $"{ex}");
                                    }
                                }
                            };

                        }
                    }

                    if (!Directory.GetDirectories(dataPath, "*").Any() &&
                        !Providers.TryGetValue(dataFolder, out _))
                    {
                        MessageBox.Query(40, 7, "Error", "Data provider not found");
                        return;
                    }

                    if (!Directory.GetDirectories(dataPath, "*").Any())
                    {
                        Providers.TryGetValue(dataFolder, out var dataProviderFactory);
                        try
                        {
                            dataProviderFactory().Init(_path);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Query(50, 20, "Error", "There was an error with getting data." +
                                                              $"{Environment.NewLine}{Environment.NewLine}" +
                                                              $"{ex}");
                        }
                    }

                };
            }

            return mainWindow;
        }
    }
}
