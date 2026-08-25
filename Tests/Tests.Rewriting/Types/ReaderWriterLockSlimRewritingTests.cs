// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Xunit;
using Xunit.Abstractions;
using ControlledReaderWriterLockSlim = Microsoft.Coyote.Rewriting.Types.Threading.ReaderWriterLockSlim;

namespace Microsoft.Coyote.Rewriting.Tests
{
    public class ReaderWriterLockSlimRewritingTests : BaseRewritingTest
    {
        public ReaderWriterLockSlimRewritingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestEveryPublicReaderWriterLockSlimMemberHasAnExactInterceptor()
        {
            Type real = typeof(ReaderWriterLockSlim);
            Type model = typeof(ControlledReaderWriterLockSlim);
            var missing = new List<string>();

            foreach (ConstructorInfo constructor in real.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!HasMatchingStatic(model, "Create", constructor.GetParameters(), real, null, instanceFirst: false))
                {
                    missing.Add($".ctor({DescribeParameters(constructor.GetParameters())})");
                }
            }

            foreach (MethodInfo method in real.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName && !method.Name.StartsWith("get_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!HasMatchingStatic(model, method.Name, method.GetParameters(), method.ReturnType, real, instanceFirst: true))
                {
                    missing.Add($"{method.Name}({DescribeParameters(method.GetParameters())})");
                }
            }

            Assert.True(missing.Count is 0,
                "The controlled ReaderWriterLockSlim model is missing exact interceptors:" + Environment.NewLine +
                string.Join(Environment.NewLine, missing));
        }

        private static bool HasMatchingStatic(Type model, string name, ParameterInfo[] parameters,
            Type expectedReturnType, Type expectedReceiverType, bool instanceFirst)
        {
            return model.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(candidate =>
            {
                ParameterInfo[] actual = candidate.GetParameters();
                int offset = instanceFirst ? 1 : 0;
                return candidate.Name == name && candidate.ReturnType == expectedReturnType &&
                    actual.Length == parameters.Length + offset && actual.Skip(offset).Zip(parameters,
                        (left, right) => left.ParameterType == right.ParameterType && left.Name == right.Name).All(x => x) &&
                    (!instanceFirst || actual[0].ParameterType == expectedReceiverType);
            });
        }

        private static string DescribeParameters(ParameterInfo[] parameters) =>
            string.Join(", ", parameters.Select(parameter => parameter.ParameterType.Name));
    }
}
