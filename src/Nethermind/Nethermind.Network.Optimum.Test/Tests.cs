using System;
using System.Threading.Tasks;
using GetOptimum;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;

namespace Nethermind.Network.Optimum.Test;

public class Tests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public async Task SubscribeForever()
    {
        using var channel = GrpcChannel.ForAddress("https://localhost:5001");
        var client = new OptimumNodeService.OptimumNodeServiceClient(channel);

        using var call = client.Subscribe(new SubscribeRequest { Topic = "test-topic" });

        await foreach (var response in call.ResponseStream.ReadAllAsync())
        {
            Console.WriteLine($"[response]: {response.Message}");
        }
    }
}
