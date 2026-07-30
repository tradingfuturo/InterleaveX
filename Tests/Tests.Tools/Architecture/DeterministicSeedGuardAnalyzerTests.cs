// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Collections.Immutable;
using System.Linq;
using InterleaveX.TestConventionAnalyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    public class DeterministicSeedGuardAnalyzerTests : BaseToolsTest
    {
        /// <summary>
        /// Stand-ins for the two types the analyzer looks for, so that a fixture compiles without
        /// referencing the product or the common test assembly.
        /// </summary>
        /// <remarks>
        /// The engine carries a static factory because that is the second way a test can build one,
        /// and it has to bind for the analyzer to see anything at all: <see cref="Analyze"/> returns
        /// only analyzer diagnostics and never asks the compilation whether it compiled, so a
        /// fixture naming a member this does not declare reports nothing and passes every test that
        /// expects nothing. 'Elsewhere' is here for the opposite reason -- to be a static method
        /// returning an engine that the analyzer must *not* treat as one of the engine's own.
        /// </remarks>
        private const string Declarations = @"
namespace Microsoft.Coyote.SystematicTesting
{
    public class TestingEngine
    {
        public static TestingEngine Create(object configuration, object test) => null;
        public static string Describe() => null;
    }

    public static class Elsewhere
    {
        public static TestingEngine Build() => null;
    }
}
namespace Microsoft.Coyote.Tests.Common.Architecture
{
    public abstract class DeterministicSeedIsolationTestsBase { }
}
";

        public DeterministicSeedGuardAnalyzerTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Theory(Timeout = 5000)]
        [InlineData(@"class T { void M() { var e = new
            Microsoft.Coyote.SystematicTesting.TestingEngine
            (); } }")]
        [InlineData(@"namespace Fixture {
            using Engine = Microsoft.Coyote.SystematicTesting.TestingEngine;
            class T { void M() { var e = new Engine(); } } }")]
        [InlineData(@"class T { void M() {
            Microsoft.Coyote.SystematicTesting.TestingEngine e = new(); } }")]
        public void TestEveryCSharpConstructionSpellingRequiresAGuard(string source)
        {
            Diagnostic diagnostic = Assert.Single(Analyze(source));
            Assert.Equal(DeterministicSeedGuardAnalyzer.MissingGuardDiagnosticId, diagnostic.Id);
        }

        [Theory(Timeout = 5000)]
        [InlineData(@"class T { void M() { var e =
            Microsoft.Coyote.SystematicTesting.TestingEngine.Create(null, null); } }")]
        [InlineData(@"namespace Fixture {
            using Engine = Microsoft.Coyote.SystematicTesting.TestingEngine;
            class T { void M() { var e = Engine.Create(null, null); } } }")]
        [InlineData(@"namespace Fixture {
            using static Microsoft.Coyote.SystematicTesting.TestingEngine;
            class T { void M() { var e = Create(null, null); } } }")]
        public void TestEveryFactorySpellingRequiresAGuard(string source)
        {
            // The hole this closes. A test that asks the engine for an instance never constructs
            // one anywhere a scan can see -- the construction is in the product assembly -- so the
            // whole convention was a step around rather than a wall.
            Diagnostic diagnostic = Assert.Single(Analyze(source));
            Assert.Equal(DeterministicSeedGuardAnalyzer.MissingGuardDiagnosticId, diagnostic.Id);
        }

        [Fact(Timeout = 5000)]
        public void TestFactoryAndGuardTogetherPass()
        {
            // Empty rather than merely free of the missing-guard diagnostic: a factory has to count
            // as a builder in both directions, or an assembly whose only builder is a factory would
            // be told its guard is stale, the guard would be deleted, and the assembly would be
            // left unguarded and green.
            var diagnostics = Analyze(@"
class Guard : Microsoft.Coyote.Tests.Common.Architecture.DeterministicSeedIsolationTestsBase { }
class T { void M() { var e = Microsoft.Coyote.SystematicTesting.TestingEngine.Create(null, null); } }");

            Assert.Empty(diagnostics);
        }

        [Fact(Timeout = 5000)]
        public void TestSanctionedBaseTestFactoryNeedsNoGuard()
        {
            var diagnostics = Analyze(@"
namespace Microsoft.Coyote.Tests.Common
{
    class BaseTest
    {
        void M() { var e = Microsoft.Coyote.SystematicTesting.TestingEngine.Create(null, null); }
    }
}");

            Assert.Empty(diagnostics);
        }

        [Fact(Timeout = 5000)]
        public void TestAFactoryDeclaredElsewhereIsNotTheEnginesOwn()
        {
            // Matching any static method that returns an engine, wherever it is declared, would
            // report every caller of a test's own helper as a builder. The helper itself builds one
            // and is reported for that; its callers build nothing.
            var diagnostics = Analyze(@"
class T { void M() { var e = Microsoft.Coyote.SystematicTesting.Elsewhere.Build(); } }");

            Assert.Empty(diagnostics);
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestAFactoryMethodGroupRequiresAGuard()
        {
            var diagnostics = Analyze(@"
class T
{
    void M()
    {
        System.Func<object, object, Microsoft.Coyote.SystematicTesting.TestingEngine> factory =
            Microsoft.Coyote.SystematicTesting.TestingEngine.Create;
        var engine = factory(null, null);
    }
}");

            Diagnostic diagnostic = Assert.Single(diagnostics);
            Assert.Equal(DeterministicSeedGuardAnalyzer.MissingGuardDiagnosticId, diagnostic.Id);
        }

        [Fact(Timeout = 5000)]
        public void TestAStaticMemberThatReturnsSomethingElseIsNotABuilder()
        {
            // The engine's own statics are not all factories, and the rule is what a member returns
            // rather than that it sits on the engine.
            var diagnostics = Analyze(@"
class T { void M() { var s = Microsoft.Coyote.SystematicTesting.TestingEngine.Describe(); } }");

            Assert.Empty(diagnostics);
        }

        [Fact(Timeout = 5000)]
        public void TestCommentsAndStringsDoNotCount()
        {
            var diagnostics = Analyze(@"
class T
{
    // new TestingEngine()
    string Text = ""DeterministicSeedIsolationTestsBase new TestingEngine()"";
}");

            Assert.Empty(diagnostics);
        }

        [Fact(Timeout = 5000)]
        public void TestBuilderAndGuardTogetherPass()
        {
            var diagnostics = Analyze(@"
class Guard : Microsoft.Coyote.Tests.Common.Architecture.DeterministicSeedIsolationTestsBase { }
class T { void M() { var e = new Microsoft.Coyote.SystematicTesting.TestingEngine(); } }");

            Assert.Empty(diagnostics);
        }

        [Fact(Timeout = 5000)]
        public void TestGuardWithoutBuilderFails()
        {
            Diagnostic diagnostic = Assert.Single(Analyze(@"
class Guard : Microsoft.Coyote.Tests.Common.Architecture.DeterministicSeedIsolationTestsBase { }"));

            Assert.Equal(DeterministicSeedGuardAnalyzer.StaleGuardDiagnosticId, diagnostic.Id);
        }

        [Fact(Timeout = 5000)]
        public void TestSanctionedBaseTestBuilderNeedsNoGuard()
        {
            var diagnostics = Analyze(@"
namespace Microsoft.Coyote.Tests.Common
{
    class BaseTest
    {
        void M() { var e = new Microsoft.Coyote.SystematicTesting.TestingEngine(); }
    }
}");

            Assert.Empty(diagnostics);
        }

        private static ImmutableArray<Diagnostic> Analyze(string source)
        {
            var tree = CSharpSyntaxTree.ParseText(
                Declarations + source,
                new CSharpParseOptions(LanguageVersion.Latest));
            var compilation = CSharpCompilation.Create(
                "AnalyzerFixture",
                new[] { tree },
                new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
                },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            // Asked before the analyzer runs, because every negative test here expects an empty
            // result and a fixture that does not compile produces one. A misspelled member or a
            // missing declaration would otherwise pass as 'the analyzer correctly said nothing'.
            var errors = compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error).ToArray();
            Assert.True(errors.Length is 0,
                "the fixture does not compile, so nothing below says anything about the analyzer: " +
                string.Join("; ", errors.Select(error => error.ToString())));

            return compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new DeterministicSeedGuardAnalyzer()))
                .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        }
    }
}
