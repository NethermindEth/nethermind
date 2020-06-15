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

using System.Linq;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor.Modules.Data.Providers
{
    public class BlocksDataProvider : IDataProvider
    {
        public BlocksDataProvider()
        {
        }
        
        public void Init(string path)
        {
            var dbOnTheRocks = new BlocksRocksDb(path, new DbConfig(), LimboLogs.Instance);
            var blocksBytes = dbOnTheRocks.GetAll();
            
            var blockDecoder = new BlockDecoder();
                var blocks = blocksBytes
                .Select(b => blockDecoder.Decode(b.Value.AsRlpStream()))
                .OrderBy(b => b.Number)
                .ToList();
            
            var window = new Window("Blocks")
            {
                X = 50,
                Y = 10,
                Width = 100,
                Height = Dim.Fill()
            };
            var y = 1;
            foreach (var block in blocks)
            {
                var blockBtn = new Button(1, y++, $"{block.Number} {block.Hash}");
                

                blockBtn.Clicked = () =>
                {
                    var window6 = new Window("block details")
                    {
                        X = 150,
                        Y = 10,
                        Width = 150,
                        Height = Dim.Fill()
                    };
                    Application.Top.Add(window6);
                            
                    var btnlabel = new Label(1, 1, $"Block {block.Number} {block.Hash}");
                    window6.Add(btnlabel);
                    Application.Run(window6);
                };
                window.Add(blockBtn);
            }
            Application.Top.Add(window);
            Application.Run(window);
        }
    }
}