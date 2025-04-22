// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.NullClient;

public class Program
{
    public static async Task Main(string[] args)
    {
        await Task.WhenAll(ServeHttp(), ServeUdp());
    }

    public static Task ServeHttp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://0.0.0.0:8547", "http://0.0.0.0:9003");

        var app = builder.Build();

        app.UseHttpsRedirection();

        app.MapGet("/ping", (HttpContext _) => "pong");

        return app.RunAsync();
    }

    public static Task ServeUdp()
    {
        // TODO
        return Task.CompletedTask;
    }
}

