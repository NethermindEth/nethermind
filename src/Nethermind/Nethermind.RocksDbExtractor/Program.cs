using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Databases;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor
{
    class Program
    {
        static void Main(string[] args)
        {
            Application.Init();
            
            var window1 = new Window ("NDM RocksDb Extractor");
            Application.Top.Add(window1);
            
            var quitBtn = new Button(3, 1, "Quit");
            quitBtn.Clicked = () =>
            {
                Application.Top.Running = false;
                Application.RequestStop();
            };

            var pathLbl = new Label(3, 3, "Enter DB path: ");
            var pathTxtField = new TextField(20, 3, 70, "");
            var okBtn = new Button(90, 3, "OK");
            
            window1.Add(quitBtn, pathLbl, pathTxtField, okBtn);

            okBtn.Clicked = () =>
            {
                var pathString = pathTxtField.Text.ToString();
                if(double.TryParse(pathString, out _))
                {
                    MessageBox.ErrorQuery(30, 6, "Error", "Path can not be a number.");
                    pathTxtField.Text = string.Empty;
                    
                } else if(string.IsNullOrEmpty(pathString))
                {
                    MessageBox.ErrorQuery(30, 6, "Error", "Path can not be empty.");
                }
                else
                {
                    var window2 = new Window("nethermind_db");
                    Application.Top.Add(window2);
                    
                    var consumerBtn = new Button(1, 1, "Consumer");
                    consumerBtn.Clicked = () =>
                    {
                        var window3 = new Window("ndm_consumer");
                        Application.Top.Add(window3);
                        
                        var consumerPath = Path.Combine(pathString, "ndm_consumer");
                        var networks = System.IO.Directory.GetDirectories(consumerPath,"*");
                        var i = 1;
                        foreach (var n in networks)
                        {
                            var network = n.Split(Path.DirectorySeparatorChar).Last();
                            var networkBtn = new Button(1, i++, $"{network}");
                            
                            networkBtn.Clicked = () =>
                            {
                                var window4 = new Window($"{network}");
                                Application.Top.Add(window4);
                                
                                var consumerNetworkPath = Path.Combine(pathString, "ndm_consumer", $"{network}");
                                var dataFolders = System.IO.Directory.GetDirectories(consumerNetworkPath,"*");
                                
                                var i = 1;
                                foreach (var d in dataFolders)
                                {
                                    var dataFolder = d.Split(Path.DirectorySeparatorChar).Last();
                                    var dataFolderBtn = new Button(1, i++, $"{dataFolder}");
                                    window4.Add(dataFolderBtn);
                                    dataFolderBtn.Clicked = () =>
                                    {
                                        var window5 = new Window($"{dataFolder}");
                                        Application.Top.Add(window5);
                                        
                                        ILogManager logger = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Trace));
                                        DbOnTheRocks dbOnTheRocks = new BlocksRocksDb(consumerNetworkPath, new DbConfig(), logger);
                                        var blocksBytes = dbOnTheRocks.GetAll();
                        
                                        var blockDecoder = new BlockDecoder();
                                        var blocks = blocksBytes
                                            .Select(b => blockDecoder.Decode(AsRlpStream(b.Value)))
                                            .OrderBy(b => b.Number);
                                
                                        var y = 1;    
                                        foreach (var block in blocks)
                                        {
                                            var blockBtn = new Button(1, y++, $"{block.Number} {block.Hash}");
                                            window5.Add(blockBtn);
                                        }
                                        Application.Run(window5);
                                    };
                                }
                                Application.Run(window4);
                            };
                            window3.Add(networkBtn);
                        }
                        Application.Run(window3);
                    };
                    var providerBtn = new Button(1, 2, "Provider");
                    providerBtn.Clicked = () =>
                    {
                        var providerPath = Path.Combine(pathString, "ndm_provider");
                        MessageBox.Query(30, 6, "Message", "Provider will be ready soon!");
                    };
                    window2.Add(consumerBtn, providerBtn);
                    Application.Run(window2);
                }
            };
            Application.Run();
        }
        private static RlpStream AsRlpStream(byte[] bytes) 
              => bytes == null ? new RlpStream(Bytes.Empty) : new RlpStream(bytes);
    }
}
