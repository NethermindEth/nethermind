// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Nethermind.Crypto;
using Nethermind.LightClient.Verifier;

[SupportedOSPlatform("browser")]
public static partial class BrowserExports
{
    private static PortableLightClient? s_client;

    [JSExport]
    public static async Task InitializeKzg(string encoded)
    {
        if (encoded.Length > 4 * (1024 * 1024 / 3 + 1)) throw new InvalidDataException("KZG setup exceeds its size limit.");
        byte[] setup = Convert.FromBase64String(encoded);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(setup),
            Convert.FromHexString("D39B9F2D047CC9DCA2DE58F264B6A09448CCD34DB967881A6713EACACF0F26B7")))
            throw new InvalidDataException("KZG setup differs from the pinned Ethereum trusted setup.");
        const string path = "/lightclient/kzg_trusted_setup.txt";
        Directory.CreateDirectory("/lightclient");
        await File.WriteAllBytesAsync(path, setup);
        await KzgPolynomialCommitments.InitializeAsync(Nethermind.Logging.NullLogger.Instance, path);
    }

    [JSExport]
    public static async Task<string> Initialize(string network, string checkpoint, string bootstrap, string chain, string head, string optimistic, string best)
    {
        PortableLightClient client = new(network, checkpoint, "/lightclient");
        await client.InitializeAsync(DecodeRecord(bootstrap), DecodeJournal(chain), DecodeRecord(head), DecodeRecord(optimistic), DecodeRecord(best));
        s_client?.Dispose();
        s_client = client;
        return client.GetSyncStatus();
    }

    [JSExport]
    public static async Task<string> Apply(string kind, string record)
    {
        await Client.ApplyAsync(kind, DecodeRecord(record));
        return Client.GetSyncStatus();
    }

    [JSExport]
    public static string GetHead(string selector) => Client.GetHead(selector);

    [JSExport]
    public static async Task<string> RecoverAfterTimeout()
    {
        await Client.RecoverAfterTimeoutAsync();
        return Client.GetSyncStatus();
    }

    [JSExport]
    public static void AuthenticateExecutionHeader(string selector, string record) => Client.AuthenticateExecutionHeader(selector, DecodeRecord(record));

    [JSExport]
    public static string VerifyAccount(string selector, string address, string proof) => Client.VerifyAccount(selector, address, proof);

    [JSExport]
    public static string VerifyStorage(string selector, string address, string accountProof, string key, string storageProof) =>
        Client.VerifyStorage(selector, address, accountProof, key, storageProof);

    [JSExport]
    public static string VerifyCode(string selector, string address, string proof, string code) => Client.VerifyCode(selector, address, proof, code);

    [JSExport]
    public static void ConfigureExecution(string relaysJson)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(relaysJson);
        Client.ConfigureExecution([.. document.RootElement.EnumerateArray().Select(value => value.GetString()!)]);
    }

    [JSExport]
    public static async Task<string> Execute(string selector, string transactionJson, bool estimateGas)
    {
        try
        {
            string result = await Client.ExecuteAsync(selector, transactionJson, estimateGas);
            return $"{{\"result\":\"{result}\"}}";
        }
        catch (Nethermind.LightClient.RpcException exception)
        {
            using MemoryStream output = new();
            using (System.Text.Json.Utf8JsonWriter writer = new(output))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("error");
                writer.WriteNumber("code", exception.Code);
                writer.WriteString("message", exception.Message);
                if (exception.RevertData is not null) writer.WriteString("data", exception.RevertData);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
    }

    [JSExport]
    public static async Task<string> ExportJournal()
    {
        (byte[] chain, byte[]? head, byte[]? optimistic, byte[]? best) = await Client.ExportJournalAsync();
        return $"{{\"chain\":\"{Convert.ToBase64String(chain)}\",\"head\":\"{(head is null ? "" : Convert.ToBase64String(head))}\",\"optimistic\":\"{(optimistic is null ? "" : Convert.ToBase64String(optimistic))}\",\"best\":\"{(best is null ? "" : Convert.ToBase64String(best))}\"}}";
    }

    private static PortableLightClient Client => s_client ?? throw new InvalidOperationException("The browser verifier is not initialized.");

    private static byte[] DecodeRecord(string value)
    {
        if (value.Length > 4 * ((PortableLightClient.MaxRecordBytes + 172) / 3 + 1))
            throw new InvalidDataException("Consensus record exceeds its size limit.");
        return Convert.FromBase64String(value);
    }

    private static byte[]? DecodeJournal(string value)
    {
        if (value.Length == 0) return null;
        if (value.Length > 4 * (PortableLightClient.MaxJournalBytes / 3 + 1))
            throw new InvalidDataException("Browser consensus journal exceeds its size limit.");
        return Convert.FromBase64String(value);
    }
}
