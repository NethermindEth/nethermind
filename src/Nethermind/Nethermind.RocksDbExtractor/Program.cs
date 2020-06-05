using System;
using System.Collections.Generic;
using System.Linq;
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
            
            var window = new Window ("NDM RocksDb Extractor");
            Application.Top.Add(window);
            
            var quitBtn = new Button(3, 1, "Quit");
            quitBtn.Clicked = () =>
            {
                Application.Top.Running = false;
                Application.RequestStop();
            };

            var pathLbl = new Label(3, 3, "Enter DB path: ");
            var path = new TextField(20, 3, 70, "");
            var applyBtn = new Button(90, 3, "OK");
            
            window.Add (pathLbl, path, quitBtn, applyBtn);

            applyBtn.Clicked = () =>
            {
                var isDouble = double.TryParse(path.Text.ToString(), out _);
                if(isDouble)
                {
                    MessageBox.ErrorQuery(30, 6, "Error", "Path can not be a number.");
                    path.Text = string.Empty;
                    
                } else if(string.IsNullOrEmpty(path.Text.ToString()))
                {
                    MessageBox.ErrorQuery(30, 6, "Error", "Path can not be empty.");
                }
                else
                {
                    // var consumerPath = @"\ndm_consumer\local";
                    // var providerPath = @"\ndm_provider\local";
                    // var mainPath = path.Text.ToString() + consumerPath;

                    // dodac sprawdzenie czy jest slash na koncu czy nie
                    
                    
                    var window2 = new Window("Clients");
                    Application.Top.Add(window2);
                    
                    var consumerBtn = new Button(1, 1, "Consumer");
                    consumerBtn.Clicked = () =>
                    {
                        var window3 = new Window("Folder");
                        Application.Top.Add(window3);
                        var path = @"D:\Workspace\Demerzel\ndm\src\nethermind\src\Nethermind\Nethermind.Runner\bin\Debug\netcoreapp3.1\nethermind_db\ndm_consumer\local";
                        string[] folders = System.IO.Directory.GetDirectories(path,"*");
                        var nameFolders1 = new List<string>();
                        var i = 1;
                        
                        foreach (var folder in folders)
                        {
                            
                            // nameFolders1.Add(folder.Split('\\').Last());
                            var name = folder.Split('\\').Last();
                            var nameBtn = new Button(1, i++, $"{name}");
                            
                            
                            
                            
                            
                            
                            nameBtn.Clicked = () =>
                            {
                                var window4 = new Window("Folder1");
                                Application.Top.Add(window4);

                            ILogManager logger = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Trace));
                            DbOnTheRocks dbOnTheRocks = new BlocksRocksDb(path, new DbConfig(), logger);
                            var blocksBytes = dbOnTheRocks.GetAll();
                            
                            var blockDecoder = new BlockDecoder();
                            var y = 1;
                            foreach (var blockBytes in blocksBytes)
                            {
                                var block = blockDecoder.Decode(AsRlpStream(blockBytes.Value));
                                var blockBtn = new Button(1, y++, $"{block}");
                                window4.Add(blockBtn);
                            }
                            
                            Application.Run(window4);

                            
                            
                            
                                
                            };
                            window3.Add(nameBtn);
                            
                        }

                        
                            
                        
                        Application.Run(window3);
                    };
                    var providerBtn = new Button(1, 2, "Provider");
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
