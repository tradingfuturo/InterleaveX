// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.SystematicTesting;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Runtime.Tests
{
    public class RuntimeDelegateTests : BaseRuntimeTest
    {
        public RuntimeDelegateTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestActionWithICoyoteRuntimeDelegate()
        {
            // Regression test: verifies that Action<ICoyoteRuntime> delegates are
            // handled correctly in CoyoteRuntime.RunTestAsync without relying on
            // contravariance through the runtime extension.
            var configuration = this.GetConfiguration().WithTestingIterations(5);
            var logWriter = new LogWriter(configuration);
            using var engine = new TestingEngine(configuration, (Action<ICoyoteRuntime>)((ICoyoteRuntime runtime) =>
            {
                Specifications.Specification.Assert(runtime != null, "Runtime should not be null.");
            }), logWriter);

            engine.Run();
            var numErrors = engine.TestReport.NumOfFoundBugs;
            Assert.True(numErrors is 0, $"Found {numErrors} unexpected bugs.");
        }

        [Fact(Timeout = 5000)]
        public void TestFuncWithICoyoteRuntimeDelegate()
        {
            // Regression test: verifies that Func<ICoyoteRuntime, Task> delegates are
            // handled correctly in CoyoteRuntime.RunTestAsync without relying on
            // contravariance through the runtime extension.
            var configuration = this.GetConfiguration().WithTestingIterations(5);
            var logWriter = new LogWriter(configuration);
            using var engine = new TestingEngine(configuration, (Func<ICoyoteRuntime, Task>)(async (ICoyoteRuntime runtime) =>
            {
                Specifications.Specification.Assert(runtime != null, "Runtime should not be null.");
                await Task.CompletedTask;
            }), logWriter);

            engine.Run();
            var numErrors = engine.TestReport.NumOfFoundBugs;
            Assert.True(numErrors is 0, $"Found {numErrors} unexpected bugs.");
        }
    }
}
