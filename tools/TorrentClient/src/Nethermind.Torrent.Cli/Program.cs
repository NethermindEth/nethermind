// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Torrent.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            TorrentClientOptions options = ParseArgs(args);
            using CancellationTokenSource cts = new();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };

            TorrentSession session = new(options, Console.WriteLine);
            await session.RunAsync(cts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static TorrentClientOptions ParseArgs(string[] args)
    {
        if (args.Length == 0 || Has(args, "--help") || Has(args, "-h"))
        {
            PrintUsage();
            Environment.Exit(args.Length == 0 ? 1 : 0);
        }

        string torrentPath = args[0];
        string output = Path.Combine(Environment.CurrentDirectory, "artifacts", "torrent-downloads");
        int maxPeers = 32;
        int port = 6881;
        bool dht = true;
        bool trackers = true;
        bool verify = true;

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--output":
                case "-o":
                    output = RequireValue(args, ref i, arg);
                    break;
                case "--max-peers":
                    maxPeers = int.Parse(RequireValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--port":
                    port = int.Parse(RequireValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--no-dht":
                    dht = false;
                    break;
                case "--no-trackers":
                    trackers = false;
                    break;
                case "--skip-verify":
                    verify = false;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }
        }

        if (maxPeers <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPeers), "Max peers must be positive.");
        }

        if (port <= 0 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be in the range 1..65535.");
        }

        return new TorrentClientOptions
        {
            TorrentPath = torrentPath,
            OutputDirectory = output,
            ListenPort = port,
            MaxPeers = maxPeers,
            EnableDht = dht,
            EnableTrackers = trackers,
            VerifyExistingData = verify,
        };
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }

    private static bool Has(string[] args, string value)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == value)
            {
                return true;
            }
        }

        return false;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: Nethermind.Torrent.Cli <file.torrent> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -o, --output <dir>   Output directory. Default: ./artifacts/torrent-downloads");
        Console.WriteLine("  --max-peers <n>      Concurrent peer connections. Default: 32");
        Console.WriteLine("  --port <n>           Port announced to trackers. Default: 6881");
        Console.WriteLine("  --no-dht             Disable DHT fallback");
        Console.WriteLine("  --no-trackers        Disable HTTP and UDP tracker announces");
        Console.WriteLine("  --skip-verify        Do not verify existing files before downloading");
    }
}
