// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

#if NET10_0_OR_GREATER
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Xunit;
using Xunit.Abstractions;
using ControlledEventWaitHandle = Microsoft.Coyote.Rewriting.Types.Threading.EventWaitHandle;

namespace Microsoft.Coyote.Rewriting.Tests
{
    /// <summary>
    /// Exact-signature gate for event constructors that the rewriter redirects to Create factories.
    /// </summary>
    public class EventWaitHandleRewritingTests : BaseRewritingTest
    {
        public EventWaitHandleRewritingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestNamedWaitHandleOptionsConstructorsHaveExactInterceptors()
        {
            Type real = typeof(EventWaitHandle);
            ConstructorInfo[] constructors = real.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(constructor => constructor.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(NamedWaitHandleOptions)))
                .ToArray();

            Assert.Equal(2, constructors.Length);
            foreach (ConstructorInfo constructor in constructors)
            {
                Assert.True(HasMatchingStatic(typeof(ControlledEventWaitHandle), "Create", constructor.GetParameters(),
                    real), "The NamedWaitHandleOptions EventWaitHandle constructor is left unrewritten: .ctor(" +
                    DescribeParameters(constructor.GetParameters()) + ").");
            }
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "ReviewRemediation")]
        public void TestOpenExistingMethodsHaveExactInterceptors()
        {
            Type real = typeof(EventWaitHandle);
            foreach (MethodInfo method in real.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name is nameof(EventWaitHandle.OpenExisting) or nameof(EventWaitHandle.TryOpenExisting)))
            {
                Assert.True(HasMatchingStatic(typeof(ControlledEventWaitHandle), method.Name, method.GetParameters(),
                    method.ReturnType), "The named EventWaitHandle method is left unrewritten: " + method.Name + "(" +
                    DescribeParameters(method.GetParameters()) + ").");
            }
        }

        private static bool HasMatchingStatic(Type model, string name, ParameterInfo[] parameters,
            Type expectedReturnType)
        {
            return model.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(candidate =>
            {
                ParameterInfo[] actual = candidate.GetParameters();
                return candidate.Name == name && candidate.ReturnType == expectedReturnType &&
                    actual.Length == parameters.Length && actual.Zip(parameters,
                        (left, right) => left.ParameterType == right.ParameterType && left.Name == right.Name).All(match => match);
            });
        }

        private static string DescribeParameters(ParameterInfo[] parameters) =>
            string.Join(", ", parameters.Select(parameter => parameter.ParameterType.Name));
    }
}
#endif
