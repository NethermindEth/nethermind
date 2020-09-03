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
using System.Net.Http;
using Mono.Terminal;
using Nethermind.Facade.Proxy;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Terminal.Gui;

namespace Nethermind.PeerView
{
    public class PeersApp : Window
    {
        private PeerInfoRow ToPeerInfoRow(PeerInfoModel model)
        {
            PeerInfoRow row = new PeerInfoRow();
            row.ClientType = model.ClientType;
            row.Reputation = 100;
            row.Host = model.Host;
            row.LastSignal = model.LastSignal;
            row.EthDetails = model.EthDetails;
            return row;
        }

        private AdminJsonRpcClientProxy _adminRpc;

        private const string DefaultUrl = "http://localhost:8545";

        public PeersApp() : base("Peers")
        {
            string[] urls = {DefaultUrl};

            var logger = LimboLogs.Instance;
            var serializer = new EthereumJsonSerializer();
            var httpClient = new HttpClient();
            var defaultHttpClient = new DefaultHttpClient(httpClient, serializer, logger, int.MaxValue);
            var proxy = new JsonRpcClientProxy(defaultHttpClient, urls, logger);

            _adminRpc = new AdminJsonRpcClientProxy(proxy);

            MenuBar menu = new MenuBar(new MenuBarItem[]
            {
                new MenuBarItem("_File", new MenuItem[]
                {
                    new MenuItem("_Quit", "", () => { Application.RequestStop(); })
                }),
            });

            ListView view = new ListView()
            {
                X = 0,
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 1,
            };
            
            view.AllowsAll();

            Add(menu, view);

            bool UpdateTimer(MainLoop mainLoop)
            {
                _adminRpc.admin_peers(true).ContinueWith(
                    t => Application.MainLoop.Invoke(() =>
                    {
                        Title = $"Last Peers Update {DateTime.Now}";
                        view.SetSourceAsync(t.Result.Result.Select(ToPeerInfoRow).OrderByDescending(r => r.Reputation).ToArray());
                    })
                );

                return true;
            }

            var token = Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(10), UpdateTimer);
        }
    }
}