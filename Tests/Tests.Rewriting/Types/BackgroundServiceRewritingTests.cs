// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

#if NET
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using ControlledBackgroundService = Microsoft.Coyote.Rewriting.Types.Hosting.BackgroundService;
using SystemBackgroundService = Microsoft.Extensions.Hosting.BackgroundService;

namespace Microsoft.Coyote.Rewriting.Tests
{
    /// <summary>
    /// A COMPLETENESS GATE over the controlled <see cref="SystemBackgroundService"/> model.
    /// <para>When the rewriter cannot find a replacement for a call it leaves the call site ALONE (see
    /// <c>MethodBodyTypeRewritingPass</c>, "No matching method found") — no error, no warning. The program
    /// keeps calling the real member, and for this type that means shutdown goes back to waiting on
    /// <c>WhenAny</c> over an infinite <c>Task.Delay</c> inside an assembly the rewriter never visits, which
    /// is precisely the false deadlock the model removes. A member added by a future .NET release is
    /// therefore a silent hole, and enumerating the real type is the only way to notice one.</para>
    /// </summary>
    public class BackgroundServiceRewritingTests : BaseRewritingTest
    {
        public BackgroundServiceRewritingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// Members deliberately NOT intercepted, each with the reason it is safe.
        /// </summary>
        private static readonly HashSet<string> ExcludedMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            // Object members. They carry no lifecycle and no wait.
            "Equals", "GetHashCode", "GetType", "ToString",
        };

        [Fact(Timeout = 5000)]
        public void TestEveryPublicMemberHasAnInterceptor()
        {
            Type real = typeof(SystemBackgroundService);
            Type model = typeof(ControlledBackgroundService);

            var missing = new List<string>();

            // Instance methods, including property accessors, are rewritten to a static of the same name
            // whose first parameter is the instance. DeclaredOnly keeps this to the type's own surface;
            // anything inherited from object is excluded above.
            foreach (MethodInfo method in real.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly))
            {
                if (ExcludedMembers.Contains(method.Name))
                {
                    continue;
                }

                if (method.IsSpecialName && !method.Name.StartsWith("get_", StringComparison.Ordinal))
                {
                    continue; // operators and the like
                }

                if (!HasMatchingStatic(model, method.Name, method.GetParameters(), method.ReturnType,
                    instanceFirst: true))
                {
                    missing.Add($"{method.Name}({DescribeParameters(method.GetParameters())})");
                }
            }

            Assert.True(
                missing.Count is 0,
                "The controlled BackgroundService model is missing an interceptor for these members, so calls " +
                "to them are left UNREWRITTEN and fall back to the real implementation:" +
                Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", missing));
        }

        /// <summary>
        /// The exclusion list must stay honest: an entry that no longer names a real member is a stale
        /// exemption that could be hiding a genuinely unintercepted method.
        /// </summary>
        [Fact(Timeout = 5000)]
        public void TestExclusionListHasNoStaleEntries()
        {
            var names = new HashSet<string>(
                typeof(SystemBackgroundService).GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static).Select(m => m.Name),
                StringComparer.Ordinal);

            var stale = ExcludedMembers.Where(e => !names.Contains(e)).ToList();

            Assert.True(
                stale.Count is 0,
                "These exclusions no longer match a member of BackgroundService and should be removed: " +
                string.Join(", ", stale));
        }

        /// <summary>
        /// The model is the first over a type outside the <c>System</c> namespace, which only works because
        /// <c>TypeRewritingPass.IsSupportedType</c> admits anything registered in the map. If that check
        /// regresses to <see langword="false"/>, every call site here is silently left alone — so assert the
        /// eligibility directly rather than inferring it from a test that happens to pass.
        /// </summary>
        [Fact(Timeout = 5000)]
        public void TestTheModelledTypeIsEligibleForRewriting()
        {
            Assert.False(
                typeof(SystemBackgroundService).Namespace.StartsWith("System", StringComparison.Ordinal),
                "BackgroundService moved into the System namespace, so this test no longer covers what it claims: " +
                "it exists to prove a NON-System modelled type is rewritable.");

            MethodInfo isSupported = typeof(TypeRewritingPass).GetMethod(
                "IsSupportedType", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(isSupported);

            var options = RewritingOptions.Create();
            var pass = new MethodBodyTypeRewritingPass(options, Array.Empty<AssemblyInfo>(),
                new Logging.MemoryLogWriter(Coyote.Configuration.Create()));

            using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(
                typeof(SystemBackgroundService).Assembly.Location);
            Mono.Cecil.TypeDefinition definition = assembly.MainModule.GetTypes()
                .Single(t => t.FullName == typeof(SystemBackgroundService).FullName);

            Assert.True(
                (bool)isSupported.Invoke(pass, new object[] { definition }),
                "BackgroundService is registered as a modelled type but the rewriter refuses to rewrite calls " +
                "to it, so every one of them is left calling the real, unmodelled implementation.");
        }

        /// <summary>
        /// The gate is only worth its name if it rejects what the REWRITER rejects: a replacement is matched
        /// on its return type as well as its parameters, and one it declines to match is not an error — the
        /// call site is simply left alone, the exact silent hole this file exists to close.
        /// </summary>
        [Fact(Timeout = 15000)]
        public void TestGateRejectsAnInterceptorWithTheWrongReturnType()
        {
            MethodInfo real = typeof(SystemBackgroundService).GetMethod(
                nameof(SystemBackgroundService.StopAsync), new[] { typeof(CancellationToken) });

            Assert.False(
                HasMatchingStatic(typeof(WrongReturnTypeModel), real.Name, real.GetParameters(),
                    real.ReturnType, instanceFirst: true),
                "The gate accepted an interceptor whose return type does not match the member it replaces, so " +
                "a call to that member would be left unrewritten with the gate still green.");
        }

        /// <summary>
        /// The same hole on the other axis: the rewriter compares parameter NAMES, so renaming one in a
        /// replacement is enough to stop it ever being found.
        /// </summary>
        [Fact(Timeout = 15000)]
        public void TestGateRejectsAnInterceptorWithARenamedParameter()
        {
            MethodInfo real = typeof(SystemBackgroundService).GetMethod(
                nameof(SystemBackgroundService.StartAsync), new[] { typeof(CancellationToken) });

            Assert.False(
                HasMatchingStatic(typeof(RenamedParameterModel), real.Name, real.GetParameters(),
                    real.ReturnType, instanceFirst: true),
                "The gate accepted an interceptor whose parameter name differs from the member it replaces, so " +
                "a call to that member would be left unrewritten with the gate still green.");
        }

        /// <summary>The real <c>StopAsync</c> returns a <see cref="Task"/>.</summary>
        private static class WrongReturnTypeModel
        {
            public static bool StopAsync(SystemBackgroundService instance, CancellationToken cancellationToken) =>
                instance.ExecuteTask is null && !cancellationToken.IsCancellationRequested;
        }

        /// <summary>The BCL names this parameter <c>cancellationToken</c>.</summary>
        private static class RenamedParameterModel
        {
            public static Task StartAsync(SystemBackgroundService instance, CancellationToken token) =>
                instance.StartAsync(token);
        }

        /// <summary>
        /// Mirrors what the rewriter's own <c>CheckMethodSignaturesMatch</c> requires of a replacement: the
        /// name, the return type, and the parameters by both type AND name.
        /// </summary>
        private static bool HasMatchingStatic(Type model, string name, ParameterInfo[] parameters,
            Type expectedReturnType, bool instanceFirst)
        {
            foreach (MethodInfo candidate in model.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal) ||
                    !SameShape(candidate.ReturnType, expectedReturnType))
                {
                    continue;
                }

                ParameterInfo[] candidateParameters = candidate.GetParameters();
                int offset = instanceFirst ? 1 : 0;
                if (candidateParameters.Length != parameters.Length + offset)
                {
                    continue;
                }

                bool match = true;
                for (int idx = 0; idx < parameters.Length; ++idx)
                {
                    ParameterInfo candidateParameter = candidateParameters[idx + offset];
                    if (!SameShape(candidateParameter.ParameterType, parameters[idx].ParameterType) ||
                        !string.Equals(candidateParameter.Name, parameters[idx].Name, StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameShape(Type left, Type right) =>
            string.Equals(left.Name, right.Name, StringComparison.Ordinal);

        private static string DescribeParameters(ParameterInfo[] parameters) =>
            string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
    }
}
#endif
