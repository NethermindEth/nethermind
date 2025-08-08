// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Exceptions;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[TestFixture]
public class NamedForkTests
{
    private ChainSpecLoader _loader;

    [SetUp]
    public void Setup()
    {
        _loader = new ChainSpecLoader(new EthereumJsonSerializer());
    }

    [Test]
    public void Named_fork_cancun_sets_all_individual_eips()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            CancunTimestamp = 0x65687fd0UL
        };

        _loader.ProcessNamedForks(parameters);

        parameters.Eip1153TransitionTimestamp.Should().Be(0x65687fd0UL);
        parameters.Eip4788TransitionTimestamp.Should().Be(0x65687fd0UL);
        parameters.Eip4844TransitionTimestamp.Should().Be(0x65687fd0UL);
        parameters.Eip5656TransitionTimestamp.Should().Be(0x65687fd0UL);
        parameters.Eip6780TransitionTimestamp.Should().Be(0x65687fd0UL);
        parameters.CancunTimestamp.Should().Be(0x65687fd0UL);
    }

    [Test]
    public void Named_fork_shanghai_sets_all_individual_eips()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            ShanghaiTimestamp = 0x64373057UL
        };

        _loader.ProcessNamedForks(parameters);

        parameters.Eip3651TransitionTimestamp.Should().Be(0x64373057UL);
        parameters.Eip3855TransitionTimestamp.Should().Be(0x64373057UL);
        parameters.Eip3860TransitionTimestamp.Should().Be(0x64373057UL);
        parameters.Eip4895TransitionTimestamp.Should().Be(0x64373057UL);
        parameters.ShanghaiTimestamp.Should().Be(0x64373057UL);
    }

    [Test]
    public void Named_fork_london_sets_all_individual_eips()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            LondonBlockNumber = 12965000L
        };

        _loader.ProcessNamedForks(parameters);

        parameters.Eip1559Transition.Should().Be(12965000L);
        parameters.Eip3198Transition.Should().Be(12965000L);
        parameters.Eip3529Transition.Should().Be(12965000L);
        parameters.Eip3541Transition.Should().Be(12965000L);
        parameters.LondonBlockNumber.Should().Be(12965000L);
    }

    [Test]
    public void Named_fork_berlin_sets_all_individual_eips()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            BerlinBlockNumber = 12244000L
        };

        _loader.ProcessNamedForks(parameters);

        parameters.Eip2565Transition.Should().Be(12244000L);
        parameters.Eip2929Transition.Should().Be(12244000L);
        parameters.Eip2930Transition.Should().Be(12244000L);
        parameters.BerlinBlockNumber.Should().Be(12244000L);
    }

    [Test]
    public void Auto_detect_cancun_from_individual_eips()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            Eip1153TransitionTimestamp = 0x65687fd0UL,
            Eip4788TransitionTimestamp = 0x65687fd0UL,
            Eip4844TransitionTimestamp = 0x65687fd0UL,
            Eip5656TransitionTimestamp = 0x65687fd0UL,
            Eip6780TransitionTimestamp = 0x65687fd0UL
        };

        _loader.ProcessNamedForks(parameters);

        parameters.CancunTimestamp.Should().Be(0x65687fd0UL);
        parameters.DencunTimestamp.Should().Be(0x65687fd0UL); // Auto-set alias
    }

    [Test]
    public void Auto_detect_shanghai_from_individual_eips()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            Eip3651TransitionTimestamp = 0x64373057UL,
            Eip3855TransitionTimestamp = 0x64373057UL,
            Eip3860TransitionTimestamp = 0x64373057UL,
            Eip4895TransitionTimestamp = 0x64373057UL
        };

        _loader.ProcessNamedForks(parameters);

        parameters.ShanghaiTimestamp.Should().Be(0x64373057UL);
    }

    [Test]
    public void Auto_detect_london_from_individual_eips()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            Eip1559Transition = 12965000L,
            Eip3198Transition = 12965000L,
            Eip3529Transition = 12965000L,
            Eip3541Transition = 12965000L
        };

        _loader.ProcessNamedForks(parameters);

        parameters.LondonBlockNumber.Should().Be(12965000L);
    }

    [Test]
    public void Auto_detect_berlin_from_individual_eips()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            Eip2565Transition = 12244000L,
            Eip2929Transition = 12244000L,
            Eip2930Transition = 12244000L
        };

        _loader.ProcessNamedForks(parameters);

        parameters.BerlinBlockNumber.Should().Be(12244000L);
    }

    [Test]
    public void Mixed_named_and_individual_eips_valid_case()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            ShanghaiTimestamp = 0x64373057UL,
            Eip4844TransitionTimestamp = 0x65687fd0UL
        };

        _loader.ProcessNamedForks(parameters);

        // Shanghai EIPs
        parameters.Eip3651TransitionTimestamp.Should().Be(0x64373057UL);
        parameters.Eip3855TransitionTimestamp.Should().Be(0x64373057UL);
        parameters.Eip3860TransitionTimestamp.Should().Be(0x64373057UL);
        parameters.Eip4895TransitionTimestamp.Should().Be(0x64373057UL);

        // Individual Cancun EIP
        parameters.Eip4844TransitionTimestamp.Should().Be(0x65687fd0UL);
    }

    [Test]
    public void Should_throw_on_conflicting_named_fork_and_individual_eip()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            CancunTimestamp = 0x65687fd0UL,
            Eip4844TransitionTimestamp = 0x66000000UL
        };

        var exception = Assert.Throws<InvalidConfigurationException>(() => _loader.ProcessNamedForks(parameters));
        exception.Message.Should().Contain("Fork 'cancun' timestamp 0x65687FD0 conflicts with Eip4844TransitionTimestamp timestamp 0x66000000");
    }

    [Test]
    public void Should_throw_on_incorrect_fork_chronology_timestamps()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            CancunTimestamp = 0x64000000UL,
            ShanghaiTimestamp = 0x65000000UL
        };

        var exception = Assert.Throws<InvalidConfigurationException>(() => _loader.ProcessNamedForks(parameters));
        exception.Message.Should().Contain("Fork timestamps must be in chronological order");
    }

    [Test]
    public void Should_throw_on_incorrect_fork_chronology_blocks()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            LondonBlockNumber = 15000000L,
            BerlinBlockNumber = 16000000L
        };

        var exception = Assert.Throws<InvalidConfigurationException>(() => _loader.ProcessNamedForks(parameters));
        exception.Message.Should().Contain("Fork block numbers must be in chronological order");
    }

    [Test]
    public void Dencun_alias_should_be_identical_to_cancun()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            DencunTimestamp = 0x65687fd0UL
        };

        _loader.ProcessNamedForks(parameters);

        parameters.DencunTimestamp.Should().Be(0x65687fd0UL);
        parameters.CancunTimestamp.Should().Be(0x65687fd0UL);

        // All Cancun/Dencun EIPs should be set
        parameters.Eip1153TransitionTimestamp.Should().Be(0x65687fd0UL);
        parameters.Eip4788TransitionTimestamp.Should().Be(0x65687fd0UL);
        parameters.Eip4844TransitionTimestamp.Should().Be(0x65687fd0UL);
        parameters.Eip5656TransitionTimestamp.Should().Be(0x65687fd0UL);
        parameters.Eip6780TransitionTimestamp.Should().Be(0x65687fd0UL);
    }

    [Test]
    public void Should_throw_on_conflicting_dencun_and_cancun()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            DencunTimestamp = 0x65687fd0UL,
            CancunTimestamp = 0x66000000UL
        };

        var exception = Assert.Throws<InvalidConfigurationException>(() => _loader.ProcessNamedForks(parameters));
        exception.Message.Should().Contain("Dencun and Cancun timestamps must be identical");
    }

    [Test]
    public void Named_fork_preserves_existing_individual_eip_if_same()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            CancunTimestamp = 0x65687fd0UL,
            Eip4844TransitionTimestamp = 0x65687fd0UL
        };

        _loader.ProcessNamedForks(parameters);

        // Should not throw and all values should match
        parameters.Eip4844TransitionTimestamp.Should().Be(0x65687fd0UL);
        parameters.CancunTimestamp.Should().Be(0x65687fd0UL);
    }

    [Test]
    public void No_auto_detection_on_partial_eip_set()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            Eip4844TransitionTimestamp = 0x65687fd0UL,
            Eip1153TransitionTimestamp = 0x65687fd0UL
            // Missing other Cancun EIPs
        };

        _loader.ProcessNamedForks(parameters);

        // Should NOT auto-detect Cancun
        parameters.CancunTimestamp.Should().BeNull();
        parameters.DencunTimestamp.Should().BeNull();
    }

    [Test]
    public void Prague_fork_sets_all_prague_eips()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            PragueTimestamp = 0x681b3057UL
        };

        _loader.ProcessNamedForks(parameters);

        parameters.Eip2537TransitionTimestamp.Should().Be(0x681b3057UL);
        parameters.Eip2935TransitionTimestamp.Should().Be(0x681b3057UL);
        parameters.Eip6110TransitionTimestamp.Should().Be(0x681b3057UL);
        parameters.Eip7002TransitionTimestamp.Should().Be(0x681b3057UL);
        parameters.Eip7251TransitionTimestamp.Should().Be(0x681b3057UL);
        parameters.Eip7623TransitionTimestamp.Should().Be(0x681b3057UL);
        parameters.Eip7702TransitionTimestamp.Should().Be(0x681b3057UL);
        parameters.PragueTimestamp.Should().Be(0x681b3057UL);
    }

    [Test]
    public void Osaka_fork_sets_all_osaka_eips()
    {
        var parameters = new ChainSpecParamsJson
        {
            ChainId = 1,
            OsakaTimestamp = 0x6f1b3057UL
        };

        _loader.ProcessNamedForks(parameters);

        parameters.Eip7594TransitionTimestamp.Should().Be(0x6f1b3057UL);
        parameters.Eip7823TransitionTimestamp.Should().Be(0x6f1b3057UL);
        parameters.Eip7825TransitionTimestamp.Should().Be(0x6f1b3057UL);
        parameters.Eip7883TransitionTimestamp.Should().Be(0x6f1b3057UL);
        parameters.Eip7918TransitionTimestamp.Should().Be(0x6f1b3057UL);
        parameters.Eip7934TransitionTimestamp.Should().Be(0x6f1b3057UL);
        parameters.Eip7939TransitionTimestamp.Should().Be(0x6f1b3057UL);
        parameters.Eip7951TransitionTimestamp.Should().Be(0x6f1b3057UL);
        parameters.OsakaTimestamp.Should().Be(0x6f1b3057UL);
    }
}
