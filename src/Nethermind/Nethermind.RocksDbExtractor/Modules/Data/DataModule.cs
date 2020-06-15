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
                ["blocks"] = () => new BlocksDataProvider()
            };
        
        private readonly string _path;

        public DataModule(string path)
        {
            _path = path;
        }

        public Window Init()
        {
            var mainWindow = new Window("local")
            {
                X = 0,
                Y = 10,
                Width = 50,
                Height = Dim.Fill()
            };
            Application.Top.Add(mainWindow);

            var dataFolders = System.IO.Directory.GetDirectories(_path, "*");

            var i = 1;
            foreach (var dataPath in dataFolders)
            {
                var dataFolder = dataPath.Split(Path.DirectorySeparatorChar).Last();
                var dataFolderBtn = new Button(1, i++, $"{dataFolder}");
                mainWindow.Add(dataFolderBtn);
                dataFolderBtn.Clicked = () =>
                {
                    // var dataWindow = new Window($"{dataFolder}")
                    // {
                    //     X = 50,
                    //     Y = 10,
                    //     Width = 50,
                    //     Height = Dim.Fill()
                    // };

                    if (!Providers.TryGetValue(dataFolder, out var dataProviderFactory))
                    {
                        MessageBox.ErrorQuery(1, 1, "Data provider", "Data provider not found");
                        return;
                    }

                    dataProviderFactory().Init(_path);
                    
                    // Application.Top.Add(dataWindow);
                    // Application.Run(dataWindow);
                };
            }
            
            return mainWindow;
        }
    }
}