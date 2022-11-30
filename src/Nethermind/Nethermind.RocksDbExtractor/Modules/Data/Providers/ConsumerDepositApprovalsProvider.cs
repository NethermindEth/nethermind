// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

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
    public class ConsumerDepositApprovalsProvider : IDataProvider
    {
        public void Init(string path)
        {
            var dbOnTheRocks = new ConsumerDepositApprovalsRocksDb(path, new DbConfig(), LimboLogs.Instance);
            var depositApprovalsBytes = dbOnTheRocks.GetAll();
            var depositApprovalDecoder = new DepositApprovalDecoder();
            var depositApprovals = depositApprovalsBytes
                .Select(b => depositApprovalDecoder.Decode(b.Value.AsRlpStream()));

            var window = new Window("Consumer deposit approvals")
            {
                X = 0,
                Y = 10,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };
            if (!depositApprovals.Any())
            {
                MessageBox.Query(40, 7, "Consumer deposit approvals", "No data." +
                                                                      $"{Environment.NewLine}(ESC to close)");
                window.FocusPrev();
                return;
            }

            var y = 1;
            foreach (var depositApproval in depositApprovals)
            {
                var depositApprovalBtn = new Button(1, y++, $"AssetName: {depositApproval.AssetName}," +
                                                            $"State: {depositApproval.State}");

                depositApprovalBtn.Clicked = () =>
                {
                    var depositApprovalDetailsWindow = new Window("Deposit approval details")
                    {
                        X = 0,
                        Y = 10,
                        Width = Dim.Fill(),
                        Height = Dim.Fill()
                    };
                    Application.Top.Add(depositApprovalDetailsWindow);
                    var serializer = new EthereumJsonSerializer();
                    var depositApprovalLbl = new Label(1, 1, serializer.Serialize(depositApproval, true));
                    depositApprovalDetailsWindow.Add(depositApprovalLbl);
                    Application.Run(depositApprovalDetailsWindow);
                };
                window.Add(depositApprovalBtn);
            }

            Application.Top.Add(window);
            Application.Run(window);
        }
    }
}
