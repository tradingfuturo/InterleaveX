// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using Microsoft.Coyote.SystematicTesting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Tests for resolving which report artifacts in a worker's directory belong to the run that just
    /// finished, which is what decides the trace a parallel run promotes as its repro.
    /// </summary>
    public class ParallelTestArtifactsTests : BaseToolsTest
    {
        public ParallelTestArtifactsTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestStemOfAFreshWorkerDirectory()
        {
            // The normal case: the coordinator emptied the directory, so the output file manager
            // assigned the first index.
            string[] files = { "App_0.txt", "App_0.trace", "App_0.schedule.coverage.txt" };
            Assert.Equal("App_0", ParallelTestArtifacts.GetArtifactStem("App", files));
        }

        [Fact(Timeout = 5000)]
        public void TestStemIsTheHighestIndexPresent()
        {
            // The directory could not be emptied, so an earlier run's artifacts are still there. This
            // run's are the ones with the highest index; promoting the stale '_0' would advertise the
            // previous run's trace as the repro for this run's bug.
            string[] files = { "App_0.trace", "App_3.trace", "App_1.trace" };
            Assert.Equal("App_3", ParallelTestArtifacts.GetArtifactStem("App", files));
        }

        [Fact(Timeout = 5000)]
        public void TestStemComparesIndexesNumerically()
        {
            // '10' beats '9' even though it sorts before it as text.
            string[] files = { "App_9.trace", "App_10.trace" };
            Assert.Equal("App_10", ParallelTestArtifacts.GetArtifactStem("App", files));
        }

        [Fact(Timeout = 5000)]
        public void TestStemIgnoresOtherAssemblies()
        {
            // A prefix that merely starts the same must not be mistaken for this assembly's, or the
            // stem would name files that are never promoted.
            string[] files = { "AppOther_7.trace", "Other_2.trace", "App_1.trace" };
            Assert.Equal("App_1", ParallelTestArtifacts.GetArtifactStem("App", files));
        }

        [Fact(Timeout = 5000)]
        public void TestStemIsNullWhenNothingMatches()
        {
            Assert.Null(ParallelTestArtifacts.GetArtifactStem("App", new[] { "Other_0.trace", "readme.md" }));
            Assert.Null(ParallelTestArtifacts.GetArtifactStem("App", System.Array.Empty<string>()));

            // An unnumbered artifact is not one the output file manager produced.
            Assert.Null(ParallelTestArtifacts.GetArtifactStem("App", new[] { "App.trace", "App_.trace" }));
        }

        [Fact(Timeout = 5000)]
        public void TestStemIgnoresArtifactsThatPredateTheRun()
        {
            // Emptying the worker directory is best effort, and a worker can report a bug without
            // leaving a trace behind. Everything present then predates the run, and promoting the
            // highest of it would advertise an earlier run's trace as this run's repro.
            string[] leftovers = { "App_0.trace", "App_3.trace" };
            Assert.Null(ParallelTestArtifacts.GetArtifactStem("App", leftovers, minimumIndex: 3));

            // One index further on is this run's, so it is promoted.
            string[] withCurrent = { "App_0.trace", "App_3.trace", "App_4.trace" };
            Assert.Equal("App_4", ParallelTestArtifacts.GetArtifactStem("App", withCurrent, minimumIndex: 3));

            // An empty directory has no baseline, so the first index the run assigns is its own.
            Assert.Equal("App_0", ParallelTestArtifacts.GetArtifactStem("App", new[] { "App_0.trace" }, minimumIndex: -1));
        }

        [Fact(Timeout = 5000)]
        public void TestStemEscapesRegexMetacharactersInTheAssemblyName()
        {
            // The assembly name goes into a pattern, so a name containing '.' — which every real one
            // does once it is more than one word — must match literally rather than as a wildcard.
            string[] files = { "MyxApp_2.trace", "My.App_1.trace" };
            Assert.Equal("My.App_1", ParallelTestArtifacts.GetArtifactStem("My.App", files));
        }
    }
}
