using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm;

namespace Nethermind.DiagTools
{
    class Program
    {
        public static async Task Main(params string[] args)
        {
            HttpClient client = new HttpClient();
            string[] transactionHashes = Directory.GetFiles(@"D:\tx_traces\nethermind").Select(Path.GetFileNameWithoutExtension).ToArray();
            for (int i = 0; i < transactionHashes.Length; i++)
            {
                try
                {
                    string gethPath = "D:\\tx_traces\\geth_" + transactionHashes[i] + ".txt";
                    string nethPath = "D:\\tx_traces\\nethermind\\" + transactionHashes[i] + ".txt";

                    Console.WriteLine($"Downloading {i} of {transactionHashes.Length}");
                    HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Post, "http://10.0.1.6:8545");
                    msg.Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"method\":\"debug_traceTransaction\",\"params\":[\"{transactionHashes[i]}\"],\"id\":42}}");
                    msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    HttpResponseMessage rsp = await client.SendAsync(msg);
                    string text = await rsp.Content.ReadAsStringAsync();
                    File.WriteAllText(gethPath, text);

                    IJsonSerializer serializer = new UnforgivingJsonSerializer();
                    WrappedTransactionTrace gethTrace = serializer.Deserialize<WrappedTransactionTrace>(text);
                    string nethText = File.ReadAllText(nethPath);
                    TransactionTrace nethTrace = serializer.Deserialize<TransactionTrace>(nethText);

                    if (gethTrace.Result.Gas != nethTrace.Gas)
                    {
                        Console.WriteLine($"Gas difference in {transactionHashes[i]} - neth {nethTrace.Gas} vs geth {gethTrace.Result.Gas}");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Failed at {i} with {e}");
                }
            }

            Console.WriteLine("Complete");
            Console.ReadLine();
        }
    }
}
