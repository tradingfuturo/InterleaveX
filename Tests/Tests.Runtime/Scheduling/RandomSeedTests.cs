// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Runtime.Tests
{
    /// <summary>
    /// Tests that every test starts its exploration from a seed that is the same on every run.
    /// </summary>
    /// <remarks>
    /// Almost every test in this repository used to explore from a seed derived from a fresh
    /// <see cref="System.Guid"/>, so a failure that needed one interleaving in fifty could not be
    /// reproduced by running it again. The seed is now derived from the test's own identity, which
    /// keeps every test exploring somewhere different while making each of them repeatable.
    ///
    /// What is checked here is the derivation rather than any particular test, because the two ways
    /// it can quietly stop working both leave the suite green: the identity becoming unreachable
    /// falls back to one seed for a whole class, and the hash being replaced by one that is
    /// randomized per process reintroduces exactly the problem this replaced.
    ///
    /// That the seed is reached at all rests on there being two places to apply it rather than on a
    /// check: <c>BaseTest.GetConfiguration</c> gives one to every configuration it hands out, and
    /// the construction of the engine gives one to any configuration that arrived without. A
    /// test would have to build both its own configuration and its own engine to escape both, and
    /// the handful that build their own engine pin a seed themselves because they assert on the
    /// exploration it produces.
    /// </remarks>
    public class RandomSeedTests : BaseRuntimeTest
    {
        public RandomSeedTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestIdentityNamesTheTestRatherThanTheClass()
        {
            // xunit exposes the running test on the concrete output helper rather than on the
            // interface, so it is reached by reflection and could stop being reachable in a version
            // bump. The fallback that would take over is per class, and a class here holds up to
            // thirty tests, so it would collapse thirty starting seeds into one without failing
            // anything. This is the check that notices.
            string identity = this.GetTestIdentity();
            Assert.False(identity is null,
                "xunit no longer exposes the running test, so seeds have silently fallen back to one per class.");
            Assert.Contains(nameof(this.TestIdentityNamesTheTestRatherThanTheClass), identity);
        }

        [Fact(Timeout = 5000)]
        public void TestIdentityDiffersBetweenTestsOfOneClass()
        {
            // The companion to the test above: both live in this class, and each finds its own name,
            // which is what makes the derived seed differ between them.
            string identity = this.GetTestIdentity();
            Assert.False(identity is null, "xunit no longer exposes the running test.");
            Assert.Contains(nameof(this.TestIdentityDiffersBetweenTestsOfOneClass), identity);
            Assert.DoesNotContain(nameof(this.TestIdentityNamesTheTestRatherThanTheClass), identity);
        }

        [Fact(Timeout = 5000)]
        public void TestSeedDerivationMatchesThePublishedVectors()
        {
            // Checked against the published FNV-1a vectors rather than against itself, because a
            // hash compared only to its own output still looks correct after being replaced by
            // 'string.GetHashCode', which is randomized per process and would make every derived
            // seed differ between two runs of the same test.
            Assert.Equal(0x811C9DC5, ComputeFnv1a(string.Empty));
            Assert.Equal(0xE40C292C, ComputeFnv1a("a"));
            Assert.Equal(0xBF9CF968, ComputeFnv1a("foobar"));
        }

        [Fact(Timeout = 5000)]
        public void TestSeedIsStableAcrossCalls()
        {
            Assert.Equal(this.GetDefaultRandomSeed(), this.GetDefaultRandomSeed());
        }

        [Fact(Timeout = 5000)]
        public void TestConfigurationWithoutASeedIsGivenOne()
        {
            var configuration = Configuration.Create();
            Assert.False(configuration.RandomGeneratorSeed.HasValue,
                "a new configuration is expected to leave the seed to whoever runs it");

            var seeded = this.WithDefaultRandomSeed(configuration);

            // Both modes name a seed, and both name the one this test reports. They differ only in
            // where it comes from: derived from the test's identity, so it is the same every run, or
            // drawn fresh, so it is not. Leaving it unset under 'random' explored just as widely but
            // put the seed only inside the runtime's generator, where the nightly run's instructions
            // for reproducing a failure could not point at it.
            Assert.True(seeded.RandomGeneratorSeed.HasValue,
                "a configuration that named no seed must be given the one this test explores from");
            Assert.Equal(this.GetDefaultRandomSeed(), seeded.RandomGeneratorSeed.Value);
        }

        [Fact(Timeout = 5000)]
        public void TestTheSeedIsReportedInBothModes()
        {
            // What the nightly instructions tell somebody to search a failing test's output for. It
            // is written once per test, by the first configuration that is seeded, and it has to be
            // the same sentence under 'random' as under a derived seed or the instructions are right
            // about only one of the two runs.
            this.WithDefaultRandomSeed(Configuration.Create());

            string reported = this.GetReportedOutput();
            Assert.False(reported is null, "xunit no longer exposes what a test wrote to its output.");
            Assert.Contains($"... Using random generator seed {this.GetDefaultRandomSeed()}.", reported,
                System.StringComparison.Ordinal);
        }

        [Fact(Timeout = 5000)]
        public void TestARandomSeedIsDrawnOnceForTheWholeTest()
        {
            // A test that builds several configurations must explore from one seed, not a different
            // one each, or the seed it reports describes only the first of them.
            var first = this.WithDefaultRandomSeed(Configuration.Create());
            var second = this.WithDefaultRandomSeed(Configuration.Create());
            Assert.Equal(first.RandomGeneratorSeed.Value, second.RandomGeneratorSeed.Value);
        }

        [Fact(Timeout = 5000)]
        public void TestConfigurationKeepsTheSeedItNames()
        {
            // Tests that pin a seed do so because they assert on the exploration it produces, so
            // defaulting must never overwrite one.
            const uint Pinned = 4711;
            var configuration = Configuration.Create().WithRandomGeneratorSeed(Pinned);
            Assert.Equal(Pinned, this.WithDefaultRandomSeed(configuration).RandomGeneratorSeed.Value);
        }

        [Fact(Timeout = 5000)]
        public void TestEachTestDrawsItsOwnSeedWhenTheRunAsksForRandom()
        {
            // What the nightly job's instructions for reproducing a failure rest on. Under 'random'
            // the seed is drawn per test, so the one a failing test reports describes that test and
            // nothing else; re-dispatching with it gives every other unpinned test that value
            // instead of the one it drew. The instructions said a seed reproduced the whole run,
            // which is true of neither mode: under 'random' no single seed describes the run, and
            // under a fixed one the value is the same everywhere rather than recovered from it.
            //
            // Two instances rather than two calls, because xunit builds one per test method and it
            // is across those that the seeds have to differ. 'TestARandomSeedIsDrawnOnceForTheWholeTest'
            // above covers the other half: within one instance the draw happens once.
            uint first = new SeedProbe().Seed;
            uint second = new SeedProbe().Seed;

            if (IsRandomSeedRequested)
            {
                Assert.NotEqual(first, second);
            }
            else
            {
                // Both probes are the same class and neither is running as a test, so both fall back
                // to the class name. A fixed seed names one value for everything; a derived one
                // derives the same value from the same identity. Either way the run is repeatable,
                // which is what the deterministic mode is for.
                Assert.Equal(first, second);
            }
        }

        /// <summary>
        /// A test that is not one, built only to be asked what seed it would explore from.
        /// </summary>
        /// <remarks>
        /// Private and nested so that xunit does not collect it, and constructed without an output
        /// helper because the identity it would find there is exactly what must not vary between the
        /// two instances above: with none, both fall back to this class's name.
        /// </remarks>
        private sealed class SeedProbe : BaseRuntimeTest
        {
            internal SeedProbe()
                : base(null)
            {
            }

            internal uint Seed => this.GetDefaultRandomSeed();
        }
    }
}
