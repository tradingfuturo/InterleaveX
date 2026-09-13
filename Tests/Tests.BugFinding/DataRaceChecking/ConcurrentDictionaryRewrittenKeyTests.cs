// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests.DataRaceChecking
{
    /// <summary>
    /// Covers a concurrent dictionary whose key type is rewritten code. Under memory-access interleaving every field
    /// read in the key's <c>Equals</c> is a scheduling point, and CoreLib calls <c>Equals</c> while it holds the
    /// dictionary's internal lock. An operation switched out there left that real lock held, so the next writer
    /// blocked its thread outside the scheduler's view and the iteration parked until the hang monitor gave up.
    /// </summary>
    public class ConcurrentDictionaryRewrittenKeyTests : BaseBugFindingTest
    {
        public ConcurrentDictionaryRewrittenKeyTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 60000)]
        public void TestWritersRacingOnARecordStructKeyNeverParkInsideTheDictionaryLock()
        {
            this.Test(async () =>
            {
                var map = new ConcurrentDictionary<SymbolKey, int>();
                var key = new SymbolKey { Symbol = "ES", Expiry = 202612, Exchange = "CME" };
                Task first = Task.Run(() =>
                {
                    for (int i = 0; i < 4; i++)
                    {
                        map.TryAdd(key, i);
                        map.TryRemove(key, out int _);
                    }
                });

                Task second = Task.Run(() =>
                {
                    for (int i = 0; i < 4; i++)
                    {
                        map.AddOrUpdate(key, 10, (_, current) => current + 1);
                        map.TryGetValue(key, out int _);
                    }
                });

                await Task.WhenAll(first, second);
                Specification.Assert(map.Count <= 1, "A single key produced more than one entry.");
            },
            configuration: this.GetConfiguration()
                .WithMemoryAccessRaceCheckingEnabled()
                .WithTestingIterations(50)
                .WithPartiallyControlledConcurrencyAllowed(false)
                .WithPartiallyControlledDataNondeterminismAllowed(false)
                .WithSystematicFuzzingFallbackEnabled(false));
        }

        /// <summary>A record struct key: its compiler-generated <c>Equals</c> reads every field.</summary>
        private readonly record struct SymbolKey
        {
            public string Symbol { get; init; }

            public int Expiry { get; init; }

            public string Exchange { get; init; }
        }
    }
}
