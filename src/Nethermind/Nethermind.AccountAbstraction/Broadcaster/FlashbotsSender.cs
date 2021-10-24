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
using Nethermind.Api;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Newtonsoft.Json;

namespace Nethermind.AccountAbstraction.Flashbots
{
    public class FlashbotsSender
    {
        private readonly HttpClient _client;
        private readonly ISigner _signer;
        private readonly ILogger _logger;

        public FlashbotsSender(HttpClient client, ISigner signer, ILogger logger)
        {
            _client = client;
            _signer = signer;
            _logger = logger;
        }
        
        public class MevBundle
        {   
            public MevBundle(long blockNumber, Transaction[] transactions, Keccak[] revertingTxHashes, UInt256? minTimestamp = null, UInt256? maxTimestamp = null)
            {
                BlockNumber = $"0x{blockNumber:X}";
                Transactions = transactions.Select(tx => Rlp.Encode(tx).ToString());
                RevertingTxHashes = revertingTxHashes;

                MinTimestamp = minTimestamp;
                MaxTimestamp = maxTimestamp;
            }

            public MevBundle(long blockNumber, string[] transactions)
            {
                BlockNumber = $"0x{blockNumber:X}";
                Transactions = transactions;
                RevertingTxHashes = Array.Empty<Keccak>();
            }
            
            [JsonProperty("txs")]
            public IEnumerable<string> Transactions { get; }
            
            [JsonProperty("revertingTxHashes", NullValueHandling = NullValueHandling.Ignore)]
            public Keccak[] RevertingTxHashes { get; }

            [JsonProperty("blockNumber")]
            public string BlockNumber { get; }
            
            [JsonProperty("maxTimestamp", NullValueHandling = NullValueHandling.Ignore)]
            public UInt256? MaxTimestamp { get; }
            
            [JsonProperty("minTimestamp", NullValueHandling = NullValueHandling.Ignore)]
            public UInt256? MinTimestamp { get; }

            public string GenerateSerializedSendBundleRequest(int id = 67)
            {
                var request = new
                {
                    jsonrpc = "2.0",
                    method = "eth_sendBundle",
                    @params = new List<MevBundle>{this},
                    id = id
                };

                return JsonConvert.SerializeObject(request);
            } 

            /*
            public void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                info.AddValue("txs", Transactions.Select(tx => Rlp.Encode(tx).Bytes));
                info.AddValue("blockNumber", BlockNumber);
                if (MinTimestamp != null) info.AddValue("minTimestamp", MinTimestamp);
                if (MaxTimestamp != null) info.AddValue("maxTimestamp", MaxTimestamp);
                if (RevertingTxHashes.Length > 0) info.AddValue("revertingTxHashes", RevertingTxHashes.Select(rtx => rtx.ToString()));
            }
            */
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
