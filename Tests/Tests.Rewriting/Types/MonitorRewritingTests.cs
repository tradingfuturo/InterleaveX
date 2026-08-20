// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using ControlledMonitor = Microsoft.Coyote.Rewriting.Types.Threading.Monitor;
using SystemMonitor = System.Threading.Monitor;

namespace Microsoft.Coyote.Rewriting.Tests
{
    public class MonitorRewritingTests : BaseRewritingTest
    {
        public MonitorRewritingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestEveryPublicMonitorMethodHasAnExactInterceptor()
        {
            var missing = new List<string>();
            MethodInfo[] candidates = typeof(ControlledMonitor).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (MethodInfo method in typeof(SystemMonitor).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                {
                    continue;
                }

                ParameterInfo[] expected = method.GetParameters();
                bool matched = candidates.Any(candidate =>
                {
                    ParameterInfo[] actual = candidate.GetParameters();
                    return candidate.Name == method.Name && candidate.ReturnType == method.ReturnType &&
                        actual.Length == expected.Length && actual.Zip(expected, (left, right) =>
                            left.ParameterType == right.ParameterType && left.Name == right.Name &&
                            left.IsIn == right.IsIn && left.IsOut == right.IsOut).All(value => value);
                });

                if (!matched)
                {
                    missing.Add(method.ToString());
                }
            }

            Assert.True(missing.Count is 0,
                "The controlled Monitor model is missing exact interceptors:" + Environment.NewLine +
                string.Join(Environment.NewLine, missing));
        }
    }
}
