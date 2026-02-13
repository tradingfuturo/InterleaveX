// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Reflection;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.SystematicTesting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    public class InvalidTestMethodDiagnosticsTests : BaseToolsTest
    {
        public InvalidTestMethodDiagnosticsTests(ITestOutputHelper output)
            : base(output)
        {
        }

        // ---- Fixture classes with intentionally invalid [Test] methods ----

        private static class PrivateMethodFixture
        {
#pragma warning disable IDE0051 // Remove unused private members
            [Test]
            private static void PrivateTestMethod()
            {
            }
#pragma warning restore IDE0051 // Remove unused private members
        }

        internal static class InternalMethodFixture
        {
            [Test]
            internal static void InternalTestMethod()
            {
            }
        }

        // ---- Tests ----

        [Fact(Timeout = 5000)]
        public void TestDiagnosticForNonPublicMethod()
        {
            // When searching for a method name that doesn't match any public [Test] method,
            // the diagnostics should detect non-public [Test] methods and warn about them.
            Configuration config = this.GetConfiguration();
            config.AssemblyToBeAnalyzed = Assembly.GetExecutingAssembly().Location;
            config.TestMethodName = "PrivateTestMethod";
            var memLogger = new MemoryLogger(VerbosityLevel.Info);
            var logWriter = new LogWriter(config);
            logWriter.SetLogger(memLogger);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                TestMethodInfo.Create(config, logWriter));

            string logOutput = memLogger.ToString();

            // Should contain a warning about the private method.
            Assert.Contains("PrivateTestMethod", logOutput);
            Assert.Contains("it is not public", logOutput);

            // Should still throw the original error.
            Assert.Contains("Cannot detect a Coyote test method name containing", exception.Message);
        }

        [Fact(Timeout = 5000)]
        public void TestDiagnosticForInternalMethod()
        {
            Configuration config = this.GetConfiguration();
            config.AssemblyToBeAnalyzed = Assembly.GetExecutingAssembly().Location;
            config.TestMethodName = "InternalTestMethod";
            var memLogger = new MemoryLogger(VerbosityLevel.Info);
            var logWriter = new LogWriter(config);
            logWriter.SetLogger(memLogger);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                TestMethodInfo.Create(config, logWriter));

            string logOutput = memLogger.ToString();

            Assert.Contains("InternalTestMethod", logOutput);
            Assert.Contains("it is not public", logOutput);
            Assert.Contains("Cannot detect a Coyote test method name containing", exception.Message);
        }

        [Fact(Timeout = 5000)]
        public void TestNoDiagnosticWhenValidTestsExist()
        {
            // When valid [Test] methods are found, the diagnostic should NOT be triggered.
            Configuration config = this.GetConfiguration();
            config.AssemblyToBeAnalyzed = Assembly.GetExecutingAssembly().Location;
            var memLogger = new MemoryLogger(VerbosityLevel.Info);
            var logWriter = new LogWriter(config);
            logWriter.SetLogger(memLogger);

            // Discover all tests - should find valid ones without triggering diagnostics.
            var testNames = TestMethodInfo.GetAllTestMethodNames(config, logWriter);
            Assert.True(testNames.Count >= 2);

            string logOutput = memLogger.ToString();

            // No diagnostic warnings should appear since valid tests exist.
            Assert.DoesNotContain("it is not public", logOutput);
        }
    }
}
