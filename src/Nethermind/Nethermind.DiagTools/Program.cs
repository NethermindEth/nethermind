using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Nethermind.DiagTools
{
    class Program
    {
        public static async Task Main(params string[] args)
        {
            HttpClient client = new HttpClient();
            string[] transactionHashes = {
                "0xaa5127fbcb48c2c44a0f8fd6ba1b3c2546094a66cdc91919e82142560d7cd2a6",
                "0xf2b877b7632149515701753edd219141c02037a8346f0e602ffc67989be51e86",
                "0x960e9eb074f3cbed41fd18416f51db259ad64c2a2dafacd8d579ae4f4dd176ea",
                "0x4068a2ef7f1f799f8637db033934630fe65a7ebf4d9103efd5a3bff4ebe53a6b",
                "0x3e53957214ca08f2110782b7d46324502adbf82d5c5cceaf6280020c29c93b81"
            };

            foreach (string transactionHash in transactionHashes)
            {
                HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Post, "http://10.0.1.6:8545");
                msg.Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"method\":\"debug_traceTransaction\",\"params\":[\"{transactionHash}\"],\"id\":42}}");
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                HttpResponseMessage rsp = await client.SendAsync(msg);
                string text = await rsp.Content.ReadAsStringAsync();
                File.WriteAllText("D:\\tx_traces\\" + transactionHash + ".txt", text);
            }
        }
    }
}
