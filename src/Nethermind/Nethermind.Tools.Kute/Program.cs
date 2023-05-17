using System.Diagnostics;
using System.Net.Http.Headers;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using CommandLine;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Tools.Kute;

interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}

class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

class Config
{
    [Option('f', "file", Required = true, HelpText = "File containing JSON RPC messages")]
    public string MessagesFile { get; }

    [Option('h', "host", Required = false, Default = "http://localhost:8551", HelpText = "Host for JSON RPC calls")]
    public string Host { get; }

    [Option('s', "secret", Required = true, HelpText = "Path to file with hex encoded secret for jwt authentication")]
    public string JwtSecretFile { get; }

    [Option('d', "dry", Required = false, Default = false, HelpText = "Only log into console")]
    public bool DryRun { get; }

    public Config(string messagesFile, string host, string jwtSecretFile, bool dryRun)
    {
        MessagesFile = messagesFile;
        Host = host;
        JwtSecretFile = jwtSecretFile;
        DryRun = dryRun;
    }
}

interface IJsonRpcMessageProvider
{
    IAsyncEnumerable<string> Messages { get; }
}

class SingeFileJsonRpcMessageProvider : IJsonRpcMessageProvider
{
    private readonly string _filePath;

    public SingeFileJsonRpcMessageProvider(Config config)
    {
        _filePath = config.MessagesFile;
    }

    public IAsyncEnumerable<string> Messages => File.ReadLinesAsync(_filePath);
}

interface IAuth
{
    string AuthToken { get; }
}

class JwtAuth : IAuth
{
    private readonly byte[] _secret;
    private readonly Lazy<string> _token;
    private readonly ISystemClock _clock;

    public string AuthToken
    {
        get => _token.Value;
    }

    public JwtAuth(ISystemClock clock, string hexSecret)
    {
        _clock = clock;

        // TODO: Check if `hexString` is an actual Hex string.
        _secret = Enumerable.Range(0, hexSecret.Length)
            .Where(x => x % 2 == 0)
            .Select(x => Convert.ToByte(hexSecret.Substring(x, 2), 16))
            .ToArray();
        _token = new Lazy<string>(GenerateAuthToken);
    }

    private string GenerateAuthToken()
    {
        var signingKey = new SymmetricSecurityKey(_secret);
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var claims = new[] { new Claim("iat", _clock.UtcNow.ToUnixTimeSeconds().ToString()), };
        var token = new JwtSecurityToken(claims: claims, signingCredentials: credentials);
        var handler = new JwtSecurityTokenHandler();

        return handler.WriteToken(token);
    }
}

interface IJsonRpcSubmitter
{
    Task Submit(string content);
}

class HttpJsonRpcSubmitter : IJsonRpcSubmitter
{
    private readonly Uri _uri;
    private readonly HttpClient _httpClient;
    private readonly IAuth _auth;

    public HttpJsonRpcSubmitter(HttpClient httpClient, IAuth auth, Config config)
    {
        _httpClient = httpClient;
        _auth = auth;
        _uri = new Uri(config.Host);
    }

    public async Task Submit(string jsonContent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _uri)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _auth.AuthToken) },
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };
        await _httpClient.SendAsync(request);
    }
}

class NullJsonRpcSubmitter : IJsonRpcSubmitter
{
    public Task Submit(string content) => Task.CompletedTask;
}

class Metrics
{
    public int Total { get; private set; }
    public int Failed { get; private set; }
    public int Responses { get; private set; }
    public IDictionary<string, int> Requests { get; } = new Dictionary<string, int>();

    public TimeSpan TotalRunningTime { get; set; }

    public void TickTotal() => Total++;
    public void TickFailed() => Failed++;
    public void TickResponses() => Responses++;

    public void TickMethod(string methodName)
    {
        if (Requests.ContainsKey(methodName))
        {
            Requests[methodName]++;
        }
        else
        {
            Requests[methodName] = 0;
        }
    }
}

interface IMetricsConsumer
{
    void ConsumeMetrics(Metrics metrics);
}

class ConsoleMetricsConsumer : IMetricsConsumer
{
    public void ConsumeMetrics(Metrics metrics)
    {
        Console.WriteLine($"""
        Total Running Time:  {metrics.TotalRunningTime}
        Results:
            Total:           {metrics.Total}
            Failures:        {metrics.Failed}
            Methods:
                Responses:   {metrics.Responses}
                Requests:    {metrics.Requests.Count}
        """);
        foreach (var (method, count) in metrics.Requests)
        {
            Console.WriteLine($"""
                    {method}: {count}
        """);
        }
    }
}

class Application
{
    private readonly Metrics _metrics = new();

    private readonly IJsonRpcMessageProvider _msgProvider;
    private readonly IJsonRpcSubmitter _submitter;
    private readonly IMetricsConsumer _metricsConsumer;

    public Application(
        IJsonRpcMessageProvider msgProvider,
        IJsonRpcSubmitter submitter,
        IMetricsConsumer metricsConsumer
    )
    {
        _msgProvider = msgProvider;
        _submitter = submitter;
        _metricsConsumer = metricsConsumer;
    }

    public async Task Run()
    {
        Stopwatch timer = new();
        await foreach (var msg in _msgProvider.Messages)
        {
            _metrics.TickTotal();

            var rpc = JsonSerializer.Deserialize<JsonDocument>(msg);
            if (rpc is null)
            {
                _metrics.TickFailed();
                continue;
            }

            if (rpc.RootElement.TryGetProperty("response", out _))
            {
                _metrics.TickResponses();
            }

            if (rpc.RootElement.TryGetProperty("method", out var jsonMethodField))
            {
                var methodName = jsonMethodField.GetString();
                if (methodName is not null)
                {
                    _metrics.TickMethod(methodName);
                }


                await _submitter.Submit(msg);
            }
        }

        _metrics.TotalRunningTime = timer.Elapsed;

        _metricsConsumer.ConsumeMetrics(_metrics);
    }
}

static class Program
{
    public static async Task Main(string[] args)
    {
        Config config = Parser.Default.ParseArguments<Config>(args).Value;
        IServiceProvider serviceProvider = BuildServiceProvider(config);
        Application app = serviceProvider.GetService<Application>()!;

        await app.Run();
    }

    static IServiceProvider BuildServiceProvider(Config config)
    {
        IServiceCollection collection = new ServiceCollection();

        collection.AddSingleton(config);
        collection.AddSingleton<Application>();
        collection.AddSingleton<ISystemClock, SystemClock>();
        collection.AddSingleton<IAuth, JwtAuth>();
        collection.AddSingleton<IJsonRpcMessageProvider, SingeFileJsonRpcMessageProvider>();
        if (config.DryRun)
        {
            collection.AddSingleton<IJsonRpcSubmitter, NullJsonRpcSubmitter>();
        }
        else
        {
            collection.AddSingleton<IJsonRpcSubmitter, HttpJsonRpcSubmitter>();
        }

        collection.AddSingleton<IMetricsConsumer, ConsoleMetricsConsumer>();

        return collection.BuildServiceProvider();
    }
}
