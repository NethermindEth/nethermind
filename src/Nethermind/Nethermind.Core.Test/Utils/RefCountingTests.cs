using System.Threading;
using FluentAssertions;
using Nethermind.Core.Utils;
using NUnit.Framework;

namespace Nethermind.Core.Test.Utils;

public class RefCountingTests
{
    private class TestRefCounting : RefCountingDisposable
    {
        private const int Used = 0;
        private const int Cleaned = 1;

        private int _cleaned = Used;
        private int _tryCount;

        public long TryCount => _tryCount;

        public bool Try()
        {
            Interlocked.Increment(ref _tryCount);
            return TryAcquireLease();
        }

        protected override void CleanUp()
        {
            int existing = Interlocked.Exchange(ref _cleaned, Cleaned);

            // should be called only once and set it to used
            existing.Should().Be(Used);
        }
    }

    [Test]
    public void Two_threads()
    {
        const int sleepInMs = 100;

        TestRefCounting counter = new();

        Thread thread1 = new(LeaseRelease);
        Thread thread2 = new(LeaseRelease);

        thread1.Start();
        thread2.Start();

        Thread.Sleep(sleepInMs);

        // dispose once
        counter.Dispose();

        thread1.Join();
        thread2.Join();

        const int minLeasesPerSecond = 1_000_000;
        const int msInSec = 1000;
        const int minLeaseCount = minLeasesPerSecond * sleepInMs / msInSec;

        counter.TryCount.Should().BeGreaterThan(minLeaseCount,
            $"On modern CPUs the speed of lease should be bigger than {minLeasesPerSecond} / s");

        void LeaseRelease()
        {
            while (counter.Try())
            {
                // after lease, dispose
                counter.Dispose();
            }
        }
    }
}
