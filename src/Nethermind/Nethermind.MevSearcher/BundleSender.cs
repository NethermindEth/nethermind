//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Nethermind.MevSearcher.Data;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.MevSearcher
{
    public class BundleSender : IBundleSender
    {
        private readonly HttpClient _client;
        private readonly ISigner _signer;
        private readonly ILogger _logger;

        public BundleSender(HttpClient client, ISigner signer, ILogger logger)
        {
            _client = client;
            _signer = signer;
            _logger = logger;
        }
        
        public async Task SendBundle(MevBundle bundle, string endpoint)
        {
            Address address = _signer.Address;

            string serializedRequest = bundle.GenerateSerializedSendBundleRequest();
            Signature signature = SignMessage(serializedRequest, _signer);

            HttpRequestMessage request = new HttpRequestMessage
            {
                RequestUri = new Uri(endpoint), 
                Method = HttpMethod.Post,
                Headers = {
                {
                    "X-Flashbots-Signature", $"{address}:{signature}"
                }
                },
                Content = new StringContent(serializedRequest, Encoding.UTF8, "application/json")

            };
            HttpResponseMessage response = await _client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                if (_logger!.IsInfo) _logger.Info($"Bundle with {bundle.Transactions.Count()} transactions sent successfully");
            }
            else
            {
                if (_logger!.IsWarn) _logger.Warn($"eth_sendBundle failed with status code {response.StatusCode} with message {await new StreamReader(await response.Content.ReadAsStreamAsync()).ReadToEndAsync()}");
            }
        }

        public static Signature SignMessage(string messageToSign, ISigner signer)
        {
            Keccak hashedRequest = Keccak.Compute(messageToSign);

            byte[] hashedBytes = Encoding.UTF8.GetBytes(hashedRequest.ToString());

            List<byte> byteList = new();
            byte[] version = Encoding.UTF8.GetBytes("\x19");
            byte[] header = Encoding.UTF8.GetBytes("Ethereum Signed Message:\n" + hashedBytes.Length);
            byteList.AddRange(version);
            byteList.AddRange(header);
            byteList.AddRange(hashedBytes);

            Signature signature = signer.Sign(Keccak.Compute(byteList.ToArray()));
            return signature;
        }
    }
}
