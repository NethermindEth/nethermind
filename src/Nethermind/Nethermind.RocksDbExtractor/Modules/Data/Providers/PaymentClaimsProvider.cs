// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
using System.Linq;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.RocksDbExtractor.ProviderDecoders;
using Nethermind.RocksDbExtractor.ProviderDecoders.RocksDb;
using Nethermind.Serialization.Json;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor.Modules.Data.Providers
{
    public class PaymentClaimsProvider : IDataProvider
    {
        public void Init(string path)
        {
            var dbOnTheRocks = new PaymentClaimsRocksDb(path, new DbConfig(), LimboLogs.Instance);
            var paymentClaimsBytes = dbOnTheRocks.GetAll();
            var paymentClaimsDecoder = new PaymentClaimDecoder();
            var paymentClaims = paymentClaimsBytes
                .Select(b => paymentClaimsDecoder.Decode(b.Value.AsRlpStream()));

            var window = new Window("Payment claims") { X = 0, Y = 10, Width = Dim.Fill(), Height = Dim.Fill() };
            if (!paymentClaims.Any())
            {
                MessageBox.Query(40, 7, "Payment claims", "No data." +
                                                          $"{Environment.NewLine}(ESC to close)");
                window.FocusPrev();
                return;
            }

            var y = 1;
            foreach (var paymentClaim in paymentClaims)
            {
                var paymentClaimBtn = new Button(1, y++, $"AssetName: {paymentClaim.AssetName}," +
                                                         $"DepositId: {paymentClaim.DepositId}");

                paymentClaimBtn.Clicked = () =>
                {
                    var paymentClaimsDetailsWindow = new Window("Payment claim details")
                    {
                        X = 0,
                        Y = 10,
                        Width = Dim.Fill(),
                        Height = Dim.Fill()
                    };
                    Application.Top.Add(paymentClaimsDetailsWindow);

                    var serializer = new EthereumJsonSerializer();
                    var paymentClaimLbl = new Label(1, 1, serializer.Serialize(paymentClaim, true));
                    paymentClaimsDetailsWindow.Add(paymentClaimLbl);
                    Application.Run(paymentClaimsDetailsWindow);
                };
                window.Add(paymentClaimBtn);
            }

            Application.Top.Add(window);
            Application.Run(window);
        }
    }
}
