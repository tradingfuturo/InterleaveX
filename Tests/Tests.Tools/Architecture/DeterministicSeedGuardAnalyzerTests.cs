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
        private const string Declarations = @"
namespace Microsoft.Coyote.SystematicTesting
{
    public class TestingEngine { }
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

            return compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new DeterministicSeedGuardAnalyzer()))
                .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        }
    }
}
