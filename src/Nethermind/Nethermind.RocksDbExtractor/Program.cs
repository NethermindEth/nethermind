using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
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
            
            var top = Application.Top;
            var win = new Window ("NDM RocksDb Extractor");
            top.Add(win);
            
            var quitBtn = new Button(3, 1, "Quit");
            quitBtn.Clicked = () =>
            {
                top.Running = false;
            };

            var pathLbl = new Label(3, 3, "Enter DB path: ");
            var path = new TextField(20, 3, 70, "");
            var applyBtn = new Button(90, 3, "Apply");

            applyBtn.Clicked = () =>
            {
                double number = 0;
                var isDouble = double.TryParse(path.Text.ToString(), out number);
                if(isDouble)
                {
                    MessageBox.ErrorQuery(30, 6, "Error", "Path can not be a number.");
                    path.Text = string.Empty;
                    return;
                } else if(string.IsNullOrEmpty(path.Text.ToString()))
                {
                    MessageBox.ErrorQuery(30, 6, "Error", "Path can not be empty.");
                    return;
                }
                else
                {
                    var clientsListView = new ListView(new Rect(3, 8, top.Frame.Width, 10), new List<string>()
                        {
                            "Consumer",
                            "Provider"
                        });

                    win.Add(
                        clientsListView
                    );
                    // D:\Workspace\Demerzel\ndm\src\nethermind\src\Nethermind\Nethermind.Runner\bin\Debug\netcoreapp3.1\nethermind_db\ndm_consumer\local
                    
                    // var isEnter = Console.ReadKey().Key == ConsoleKey.Enter;
                    
            
                    // ILogManager logger = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Trace));
                    // DbOnTheRocks dbOnTheRocks = new BlocksRocksDb(path, new DbConfig(), logger);
                    // var blocksBytes = dbOnTheRocks.GetAll();
                    //
                    // var blockDecoder = new BlockDecoder();
                    // foreach (var blockBytes in blocksBytes)
                    // {
                    //     var block = blockDecoder.Decode(AsRlpStream(blockBytes.Value));
                    //     Console.WriteLine(block);
                    // }
                            
                    
                    
                    
                    
                    // MessageBox.Query(30, 6, "Query", $"Path: {path.Text.ToString()}.");
                }
                
                
                
                
                
                //
                // win.Remove(allMoviesListView);
                // if (string.IsNullOrEmpty(txtSearch.Text.ToString()) || string.IsNullOrEmpty(minimumRatingTxt.Text.ToString()))
                // {
                //     win.Add(allMoviesListView);
                // }
                // else
                // {
                //     win.Remove(allMoviesListView);
                //     
                // }
            
            };
            
            // Console.ReadKey();
            // if (Console.ReadKey().Key == ConsoleKey.Enter)
            // {

            //
            //     // D:\Workspace\Demerzel\ndm\src\nethermind\src\Nethermind\Nethermind.Runner\bin\Debug\netcoreapp3.1\nethermind_db\ndm_consumer\local
            // }
            
 



            // // Add some controls

            // var forKidsOnly = new CheckBox(3, 3, "For Kids?");
            // var minimumRatingLbl = new Label(25, 3, "Minimum Rating: ");
            // var minimumRatingTxt = new TextField(41, 3, 10, "");
            
            // var mylist = new List<string>();
            // mylist.Add("aa");
            // mylist.Add("bb");
            // mylist.Add("cc");
            // var allMoviesListView = new ListView(new Rect(4, 8, top.Frame.Width, 200), mylist);
            //
            
            win.Add (
                pathLbl,
                path,
                quitBtn,
                applyBtn
            );

            Application.Run(win);
        }
        private static RlpStream AsRlpStream(byte[] bytes) 
            => bytes == null ? new RlpStream(Bytes.Empty) : new RlpStream(bytes);
    }
}
    
    
    
    
    
    
    
    
    
    
    
    
    // class Program
    // {
    //     static void Main(string[] args)
    //     {
    //         var top = Application.Top;
    //
    //         // Creates a menubar, the item "New" has a help menu.
    //         var menu = new MenuBar (new MenuBarItem [] {
    //             new MenuBarItem ("_File", new MenuItem [] {
    //                 new MenuItem ("_Quit", "", () => { top.Running = false; })
    //             })
    //         });
    //         top.Add(menu);
    //
    //         var result = MessageBox.Query (50, 7,
    //             "Question", "Select client", "Consumer", "Provider");
    //
    //         switch (result)
    //         {
    //             case 0:
    //             {
    //                 // var path = @"D:\Workspace\Demerzel\ndm\src\nethermind\src\Nethermind\Nethermind.Runner\bin\Debug\netcoreapp3.1\nethermind_db\ndm_consumer\local";
    //
    //                 break;
    //             }
    //             case 1:
    //             {
    //                 // var path = @"D:\Workspace\Demerzel\ndm\src\nethermind\src\Nethermind\Nethermind.Runner\bin\Debug\netcoreapp3.1\nethermind_db\ndm_consumer\local";
    //
    //                 break;
    //             }
    //         }
    //         
    //         return;
    //         
    //         // var path = @"D:\Workspace\Demerzel\ndm\src\nethermind\src\Nethermind\Nethermind.Runner\bin\Debug\netcoreapp3.1\nethermind_db\ndm_consumer\local";
    //         // Console.WriteLine("Consumer");
    //         // Console.WriteLine("Provider");
    //         //
    //         // var isEnter = Console.ReadKey().Key == ConsoleKey.Enter;
    //         
    //
    //         // ILogManager logger = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Trace));
    //         // DbOnTheRocks dbOnTheRocks = new BlocksRocksDb(path, new DbConfig(), logger);
    //         // var blocksBytes = dbOnTheRocks.GetAll();
    //         //
    //         // var blockDecoder = new BlockDecoder();
    //         // foreach (var blockBytes in blocksBytes)
    //         // {
    //         //     var block = blockDecoder.Decode(AsRlpStream(blockBytes.Value));
    //         //     Console.WriteLine(block);
    //         //     // Console.WriteLine($"{keyValuePair.Key.ToHexString()}->{keyValuePair.Value.ToHexString()}");
    //         // }
    //         
    //         
    //         
    //         
    //         
    //         
    //         
    //         
    //         
    //         // string path = @"D:\Workspace\Demerzel\ndm\src\nethermind\src\Nethermind\Nethermind.Runner\bin\Debug\netcoreapp3.1\nethermind_db\ndm_consumer\local\blocks";
    //         // ILogManager logger = new OneLoggerLogManager(new ConsoleAsyncLogger(LogLevel.Trace));
    //         // DbOnTheRocks dbOnTheRocks = new BlocksRocksDb(path, new DbConfig(), logger);
    //         // DbOnTheRocks dbOnTheRocks = new CodeRocksDb(path, new DbConfig(), logger);
    //         // DbOnTheRocks dbOnTheRocks = new StateRocksDb(path, new DbConfig(), logger);
    //         // var allValues = dbOnTheRocks.GetAll();
    //         // foreach (KeyValuePair<byte[], byte[]> keyValuePair in allValues)
    //         // {
    //         //     Console.WriteLine($"{keyValuePair.Key.ToHexString()}->{keyValuePair.Value.ToHexString()}");
    //         //     Console.WriteLine("");
    //         //     Console.WriteLine("");
    //         // }
    //         // var result = dbOnTheRocks[new byte[] {1, 2, 3}];
    //         // dbOnTheRocks[new byte[] {1, 2, 3}] = new byte[] {4, 5, 6};
    //         // Console.WriteLine(result.ToHexString());
    //         // Console.ReadLine();
    //     }
    //

    // }
// }