using System;
using System.IO;
using System.Net;
using System.Net.Http;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Providers.Plugins.JsonRpc
{
    public class JsonRpcClient : IJsonRpcClient
    {
        private readonly string _host;
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;

        public JsonRpcClient(string host, ILogger logger, HttpClient? httpClient = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _httpClient = httpClient ?? new HttpClient();
            _logger = logger;

            _httpClient.BaseAddress = new Uri(_host);
        }

        public string PostAsync(string request)
        {
            return Send(request);
        }

        private string Send(string jsonRequest)
        {
            var httpWebRequest = (HttpWebRequest)WebRequest.Create(_host);
            httpWebRequest.ContentType = "application/json";
            httpWebRequest.Method = "POST";

            using (var streamWriter = new StreamWriter(httpWebRequest.GetRequestStream()))
            {
                streamWriter.Write(jsonRequest);
            }

            var httpResponse = (HttpWebResponse)httpWebRequest.GetResponse();
            string result;
            using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
            {
                result = streamReader.ReadToEnd();
            }

            return result;
        }
    }
}