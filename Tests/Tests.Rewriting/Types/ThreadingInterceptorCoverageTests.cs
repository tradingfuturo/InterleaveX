// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using ControlledCancellationTokenRegistration = Microsoft.Coyote.Rewriting.Types.Threading.CancellationTokenRegistration;
using ControlledCancellationTokenSource = Microsoft.Coyote.Rewriting.Types.Threading.CancellationTokenSource;
using ControlledManualResetEventSlim = Microsoft.Coyote.Rewriting.Types.Threading.ManualResetEventSlim;
using ControlledTask = Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task;
using ControlledTimer = Microsoft.Coyote.Rewriting.Types.Threading.Timer;
using ControlledTimerInterface = Microsoft.Coyote.Rewriting.Types.Threading.TimerInterface;

namespace Microsoft.Coyote.Rewriting.Tests
{
    /// <summary>
    /// Completeness gate for the threading models: every framework overload that has to be controlled has an
    /// interceptor with exactly its signature.
    /// </summary>
    /// <remarks>
    /// The signature conformance test proves that every interceptor matches some framework member. This proves the
    /// converse, which nothing else does. The rewriter redirects a call only to an interceptor with exactly the
    /// overload's signature, so an overload without one is silently left to the framework - which for these types
    /// means an uncontrolled callback or wait at every call site that happens to use it.
    /// </remarks>
    public class ThreadingInterceptorCoverageTests : BaseRewritingTest
    {
        public ThreadingInterceptorCoverageTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestEveryTaskContinueWithOverloadHasAnInterceptor()
        {
            AssertInstanceMethodsIntercepted(typeof(Task), typeof(ControlledTask), nameof(Task.ContinueWith), 20);
            AssertInstanceMethodsIntercepted(typeof(Task<>), typeof(Microsoft.Coyote.Rewriting.Types.Threading.Tasks.Task<>),
                nameof(Task.ContinueWith), 20);
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestEveryTimerMemberHasAnInterceptor()
        {
            ConstructorInfo[] constructors = typeof(Timer).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Assert.Equal(5, constructors.Length);
            foreach (ConstructorInfo constructor in constructors)
            {
                Assert.True(HasInterceptor(typeof(ControlledTimer), "Create", null, constructor.GetParameters(),
                    typeof(Timer)), $"The Timer constructor is left to the framework: .ctor({Describe(constructor)}).");
            }

            AssertInstanceMethodsIntercepted(typeof(Timer), typeof(ControlledTimer), nameof(Timer.Change), 4);
            AssertInstanceMethodsIntercepted(typeof(Timer), typeof(ControlledTimer), nameof(Timer.Dispose), 2);
            AssertInstanceMethodsIntercepted(typeof(Timer), typeof(ControlledTimer), nameof(Timer.DisposeAsync), 1);
            AssertInstanceMethodsIntercepted(typeof(ITimer), typeof(ControlledTimerInterface), nameof(ITimer.Change), 1);
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestEveryBlockingOrStatefulManualResetEventSlimMemberHasAnInterceptor()
        {
            // IsSet and SpinCount are deliberately absent: the model reads the framework event's own state, so a
            // read of either neither blocks nor bypasses anything the model keeps.
            Type real = typeof(ManualResetEventSlim);
            Type model = typeof(ControlledManualResetEventSlim);
            AssertInstanceMethodsIntercepted(real, model, nameof(ManualResetEventSlim.Set), 1);
            AssertInstanceMethodsIntercepted(real, model, nameof(ManualResetEventSlim.Reset), 1);
            AssertInstanceMethodsIntercepted(real, model, nameof(ManualResetEventSlim.Wait), 6);
            AssertInstanceMethodsIntercepted(real, model, nameof(ManualResetEventSlim.Dispose), 1);
            AssertInstanceMethodsIntercepted(real, model, "get_" + nameof(ManualResetEventSlim.WaitHandle), 1);
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "CurrentUseThreadingModels")]
        public void TestAsynchronousCancellationMembersHaveInterceptors()
        {
            AssertInstanceMethodsIntercepted(typeof(CancellationTokenSource), typeof(ControlledCancellationTokenSource),
                nameof(CancellationTokenSource.CancelAsync), 1);
            AssertInstanceMethodsIntercepted(typeof(CancellationTokenRegistration),
                typeof(ControlledCancellationTokenRegistration), nameof(CancellationTokenRegistration.DisposeAsync), 1);
        }

        private static void AssertInstanceMethodsIntercepted(Type real, Type model, string name, int expectedCount)
        {
            MethodInfo[] overloads = real.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == name).ToArray();
            Assert.Equal(expectedCount, overloads.Length);
            foreach (MethodInfo overload in overloads)
            {
                Assert.True(HasInterceptor(model, name, real, overload.GetParameters(), overload.ReturnType),
                    $"{real.Name}.{name}({Describe(overload)}) is left to the framework.");
            }
        }

        /// <summary>
        /// Returns whether the model declares a public static method with the specified name, receiver, parameters
        /// and return type. Types are compared by their display form, so that generic parameters of the framework
        /// type and of the model compare equal when they are named alike, which is how both are written.
        /// </summary>
        private static bool HasInterceptor(Type model, string name, Type receiver, ParameterInfo[] parameters,
            Type returnType)
        {
            int offset = receiver is null ? 0 : 1;
            foreach (MethodInfo candidate in model.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                ParameterInfo[] actual = candidate.GetParameters();
                if (candidate.Name != name || actual.Length != parameters.Length + offset ||
                    candidate.ReturnType.ToString() != returnType.ToString())
                {
                    continue;
                }

                if (receiver != null)
                {
                    Type receiverType = actual[0].ParameterType;
                    if ((receiverType.IsByRef ? receiverType.GetElementType() : receiverType).ToString() != receiver.ToString())
                    {
                        continue;
                    }
                }

                bool isMatch = true;
                for (int idx = 0; idx < parameters.Length && isMatch; idx++)
                {
                    isMatch = actual[idx + offset].ParameterType.ToString() == parameters[idx].ParameterType.ToString() &&
                        actual[idx + offset].Name == parameters[idx].Name;
                }

                if (isMatch)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Describe(MethodBase method) =>
            string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name));
    }
}
#endif
