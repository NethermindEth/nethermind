using System.IO;
using System.Linq;
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
            string[] transactionHashes = Directory.GetFiles(@"D:\tx_traces\nethermind").Select(Path.GetFileNameWithoutExtension).ToArray();
            foreach (string transactionHash in transactionHashes)
            {
                HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Post, "http://10.0.1.6:8545");
                msg.Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"method\":\"debug_traceTransaction\",\"params\":[\"{transactionHash}\"],\"id\":42}}");
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                HttpResponseMessage rsp = await client.SendAsync(msg);
                string text = await rsp.Content.ReadAsStringAsync();
                File.WriteAllText("D:\\tx_traces\\geth_" + transactionHash + ".txt", text);
            }
        }
    }
}
