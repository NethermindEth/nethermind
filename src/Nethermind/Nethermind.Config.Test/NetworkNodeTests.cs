// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Config.Test;

[NonParallelizable]
public class NetworkNodeTests
{
    [Test]
    public void ParseNodes_masks_invalid_node_and_exception_when_enabled([Values] bool maskSensitiveData)
    {
        bool previous = SensitiveLogMasking.Enabled;
        try
        {
            SensitiveLogMasking.Enabled = maskSensitiveData;
            InterfaceLogger sink = Substitute.For<InterfaceLogger>();
            sink.IsError.Returns(true);
            string? message = null;
            Exception? loggedException = null;
            sink.When(x => x.Error(Arg.Any<string>(), Arg.Any<Exception?>()))
                .Do(call =>
                {
                    message = call.ArgAt<string>(0);
                    loggedException = call.ArgAt<Exception?>(1);
                });

            const string invalidNode = "enode://invalid@10.11.12.13:30303";
            NetworkNode[] nodes = NetworkNode.ParseNodes([invalidNode], new ILogger(sink));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(nodes, Is.Empty);
                Assert.That(message, Does.Contain(maskSensitiveData ? "[redacted]" : invalidNode));
                if (maskSensitiveData) Assert.That(message, Does.Not.Contain(invalidNode));
                Assert.That(loggedException, maskSensitiveData ? Is.Null : Is.Not.Null);
            }
        }
        finally
        {
            SensitiveLogMasking.Enabled = previous;
        }
    }
}
