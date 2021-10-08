using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Newtonsoft.Json.Linq;

namespace Nethermind.DataMarketplace.Providers.Plugins.WebApi
{
public class NdmWebApiPlugin : INdmWebApiPlugin
    {
        private static readonly IDictionary<string, string> EmptyQueryString = new Dictionary<string, string>();
        private static readonly string JsonContentType = "application/json";
        private static readonly string ErrorWildcard = "*";
        private static readonly string EmptyJson = "{}";
        private readonly HttpClient _client = new HttpClient();
        private bool _initialized;
        private ILogger? _logger;
        public string? Name { get; private set; }
        public string? Type { get; private set; }
        public string? Url { get; private set; }
        public string? Method { get; private set; }
        public IDictionary<string, string>? Headers { get; private set; }
        public IDictionary<string, string>? Errors { get; private set; }
        public IDictionary<string, string>? QueryString { get; private set; }

        public Task InitAsync(ILogManager logManager)
        {
            if (string.IsNullOrWhiteSpace(Method))
            {
                throw new Exception($"HTTP method was not specified for NDM plugin: {Name}");
            }

            _logger = logManager.GetClassLogger();
            if (!string.IsNullOrWhiteSpace(Url))
            {
                _client.BaseAddress = new Uri(Url);
            }

            foreach ((string key, string value) in Headers ?? new Dictionary<string, string>())
            {
                _client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
            }

            _initialized = true;
            if (_logger.IsInfo) _logger.Info($"Initialized NDM Web API plugin: {Name}, URL: {Url}, method: {Method.ToUpperInvariant()}");

            return Task.CompletedTask;
        }

        public Task<string?> QueryAsync(IEnumerable<string> args)
        {
            if (!_initialized)
            {
                return Task.FromResult<string?>(string.Empty);
            }

            if (args is null)
            {
                return Task.FromResult<string?>(string.Empty);
            }

            return Method switch
            {
                "get" => InvokeGetAsync(args),
                "post" => InvokePostAsync(args),
                _ => Task.FromResult<string?>(null)
            };
        }

        public Task SubscribeAsync(Action<string> callback, Func<bool> enabled, IEnumerable<string> args,
            CancellationToken? token = null)
            => Task.CompletedTask;

        private async Task<string?> InvokeGetAsync(IEnumerable<string> args)
        {
            string endpoint = BuildEndpoint(args.FirstOrDefault());
            HttpResponseMessage response = await _client.GetAsync(endpoint);

            return await ValidateResponseAsync(response);
        }

        private async Task<string?> InvokePostAsync(IEnumerable<string> args)
        {
            string endpoint;
            string payload;
            if (args.Count() >= 2)
            {
                endpoint = BuildEndpoint(args.ElementAt(0));
                payload = args.ElementAt(1);
            }
            else
            {
                endpoint = BuildEndpoint(string.Empty);
                payload = args.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                payload = EmptyJson;
            }

            StringContent json = new StringContent(payload, Encoding.UTF8, JsonContentType);
            HttpResponseMessage response = await _client.PostAsync(endpoint, json);

            return await ValidateResponseAsync(response);
        }

        private string BuildEndpoint(string endpoint)
            => string.IsNullOrWhiteSpace(endpoint)
                ? BuildQueryString(string.Empty)
                : BuildQueryString(endpoint.StartsWith("/") ? endpoint : $"/{endpoint}");

        private string BuildQueryString(string endpoint)
        {
            if (QueryString is null)
            {
                return endpoint;
            }

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return $"?{MapQueryString(QueryString)}";
            }

            if (!endpoint.Contains("?"))
            {
                return endpoint.EndsWith("/")
                    ? $"{endpoint.Substring(0, endpoint.Length - 1)}?{MapQueryString(QueryString)}"
                    : $"{endpoint}?{MapQueryString(QueryString)}";
            }

            string[] endpointParts = endpoint.Split("?");
            string endpointPath = endpointParts.First();
            IDictionary<string, string> endpointQueryString = endpointParts.LastOrDefault()?
                                                                  .Split("&")
                                                                  .Select(s => s.Split("="))
                                                                  .ToDictionary(s => s[0], s => s[1]) ?? EmptyQueryString;

            foreach ((string key, string value) in QueryString)
            {
                endpointQueryString[key] = value;
            }

            return endpointPath.EndsWith("/")
                ? $"{endpointPath.Substring(0, endpointPath.Length - 1)}?{MapQueryString(endpointQueryString)}"
                : $"{endpointPath}?{MapQueryString(endpointQueryString)}";

            string MapQueryString(IDictionary<string, string> queryString) =>
                string.Join("&", queryString.Select(q => $"{q.Key}={q.Value}"));
        }

        private async Task<string?> ValidateResponseAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string content = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            try
            {
                JToken result = JToken.Parse(content);
                if (Errors is null || !(result is JObject jObject))
                {
                    return result.ToString();
                }

                foreach ((string key, string value) in Errors)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (!jObject.TryGetValue(key, out JToken? existingValue))
                    {
                        continue;
                    }

                    string? errorValue = existingValue?.ToString();
                    if (string.IsNullOrWhiteSpace(errorValue))
                    {
                        continue;
                    }

                    if (value.Equals(ErrorWildcard) ||
                        value.Equals(errorValue, StringComparison.InvariantCultureIgnoreCase))
                    {
                        return null;
                    }
                }

                return result.ToString();
            }
            catch
            {
                // Ignore, probably a different format than JSON e.g. CSV.
                return content;
            }
        }
    }
}