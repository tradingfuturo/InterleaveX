// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using ControlledBlockingCollection = Microsoft.Coyote.Rewriting.Types.Collections.Concurrent.BlockingCollection<int>;

namespace Microsoft.Coyote.Rewriting.Tests
{
    /// <summary>
    /// A COMPLETENESS GATE over the controlled <see cref="BlockingCollection{T}"/> mock.
    /// <para>Why this is necessary rather than merely tidy: when the rewriter cannot find a replacement for
    /// a call it leaves the call site ALONE (see <c>MethodBodyTypeRewritingPass</c>, "No matching method
    /// found"). There is no error and no warning — the program simply keeps calling the real, blocking BCL
    /// method, which is invisible to the scheduler and reintroduces the hang this mock exists to remove. A
    /// missing overload is therefore a silent hole, and the only way to notice one is to enumerate the real
    /// type and check.</para>
    /// </summary>
    public class BlockingCollectionRewritingTests : BaseRewritingTest
    {
        public BlockingCollectionRewritingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestGenericConstructorIsRedirectedToFactory()
        {
            this.Test(() =>
            {
                object collection = new BlockingCollection<int>();
                Assert.Contains("Wrapper", collection.GetType().FullName, StringComparison.Ordinal);
            });
        }

        /// <summary>
        /// Members that are deliberately NOT intercepted, each with the reason it is safe.
        /// <para>All of them are reached through interface dispatch, which the type-rewriting pass cannot
        /// redirect — it rewrites calls whose declaring type is the known type, and a <c>callvirt</c> on
        /// <see cref="IEnumerable{T}"/> or <see cref="System.Collections.ICollection"/> does not name it.
        /// They are safe to exclude because NONE of them blocks: <c>GetEnumerator</c> on this type returns a
        /// point-in-time SNAPSHOT enumerator, quite unlike <c>GetConsumingEnumerable</c>, which does block
        /// and is intercepted.</para>
        /// </summary>
        private static readonly HashSet<string> ExcludedMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Collections.Generic.IEnumerable<T>.GetEnumerator",
            "System.Collections.IEnumerable.GetEnumerator",
            "System.Collections.ICollection.CopyTo",
        };

        [Fact(Timeout = 5000)]
        public void TestEveryPublicMemberHasAnInterceptor()
        {
            Type real = typeof(BlockingCollection<int>);
            Type mock = typeof(ControlledBlockingCollection);

            var missing = new List<string>();

            // Constructors are rewritten to a static 'Create' whose parameters match the constructor's. The
            // expected return type has to be supplied rather than read off the member: a constructor's own
            // return type is void, while its replacement hands back the constructed collection.
            foreach (ConstructorInfo ctor in real.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!HasMatchingStatic(mock, "Create", ctor.GetParameters(), real, instanceFirst: false))
                {
                    missing.Add($".ctor({DescribeParameters(ctor.GetParameters())})");
                }
            }

            // Instance methods (including property accessors) are rewritten to a static of the same name
            // whose first parameter is the instance.
            foreach (MethodInfo method in real.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName && !method.Name.StartsWith("get_", StringComparison.Ordinal))
                {
                    continue; // operators and the like
                }

                if (ExcludedMembers.Contains(method.Name))
                {
                    continue;
                }

                if (!HasMatchingStatic(mock, method.Name, method.GetParameters(), method.ReturnType,
                    instanceFirst: true))
                {
                    missing.Add($"{method.Name}({DescribeParameters(method.GetParameters())})");
                }
            }

            // Static methods keep their shape exactly.
            foreach (MethodInfo method in real.GetMethods(BindingFlags.Public | BindingFlags.Static |
                BindingFlags.DeclaredOnly))
            {
                if (ExcludedMembers.Contains(method.Name))
                {
                    continue;
                }

                if (!HasMatchingStatic(mock, method.Name, method.GetParameters(), method.ReturnType,
                    instanceFirst: false))
                {
                    missing.Add($"static {method.Name}({DescribeParameters(method.GetParameters())})");
                }
            }

            Assert.True(
                missing.Count is 0,
                "The controlled BlockingCollection mock is missing an interceptor for these members, so calls " +
                "to them are left UNREWRITTEN and fall back to the real blocking implementation:" +
                Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", missing));
        }

        /// <summary>
        /// The exclusion list must stay honest: an entry that no longer names a real member is a stale
        /// exemption that could be hiding a genuinely unintercepted method.
        /// </summary>
        [Fact(Timeout = 5000)]
        public void TestExclusionListHasNoStaleEntries()
        {
            Type real = typeof(BlockingCollection<int>);
            var names = new HashSet<string>(
                real.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.DeclaredOnly).Select(m => m.Name),
                StringComparer.Ordinal);

            var stale = ExcludedMembers.Where(e => !names.Contains(e)).ToList();

            Assert.True(
                stale.Count is 0,
                "These exclusions no longer match a member of BlockingCollection<T> and should be removed: " +
                string.Join(", ", stale));
        }

        /// <summary>
        /// The gate is only worth its name if it rejects what the REWRITER rejects. The rewriter matches a
        /// replacement on its return type as well as its parameters
        /// (<c>CheckMethodSignaturesMatch</c>), and a replacement it declines to match is not an error —
        /// the call site is simply left alone, which is the exact silent hole this file exists to close.
        /// So an interceptor with the wrong return type is every bit as invisible as a missing one, and a
        /// gate that overlooks it reports full coverage over a member that is never rewritten.
        /// </summary>
        [Fact(Timeout = 15000)]
        public void TestGateRejectsAnInterceptorWithTheWrongReturnType()
        {
            MethodInfo real = typeof(BlockingCollection<int>).GetProperty(nameof(BlockingCollection<int>.Count))
                .GetGetMethod();

            Assert.False(
                HasMatchingStatic(typeof(WrongReturnTypeMock), real.Name, real.GetParameters(),
                    real.ReturnType, instanceFirst: true),
                "The gate accepted an interceptor whose return type does not match the member it replaces, so " +
                "a call to that member would be left unrewritten with the gate still green.");
        }

        /// <summary>
        /// The same hole on the other axis. The rewriter compares parameter NAMES, not just their types
        /// (<c>CheckMethodSignaturesMatch</c>), so renaming a parameter in a replacement is enough to stop
        /// it ever being found.
        /// </summary>
        [Fact(Timeout = 15000)]
        public void TestGateRejectsAnInterceptorWithARenamedParameter()
        {
            MethodInfo real = typeof(BlockingCollection<int>).GetMethod(
                nameof(BlockingCollection<int>.TryAdd), new[] { typeof(int) });

            Assert.False(
                HasMatchingStatic(typeof(RenamedParameterMock), real.Name, real.GetParameters(),
                    real.ReturnType, instanceFirst: true),
                "The gate accepted an interceptor whose parameter name differs from the member it replaces, so " +
                "a call to that member would be left unrewritten with the gate still green.");
        }

        /// <summary>
        /// The real <see cref="BlockingCollection{T}.Count"/> getter returns an <see cref="int"/>.
        /// </summary>
        private static class WrongReturnTypeMock
        {
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
            public static bool get_Count(BlockingCollection<int> instance) => instance.Count > 0;
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        }

        /// <summary>
        /// The BCL names this parameter <c>item</c>.
        /// </summary>
        private static class RenamedParameterMock
        {
            public static bool TryAdd(BlockingCollection<int> instance, int value) => instance.TryAdd(value);
        }

        /// <summary>
        /// Mirrors what the rewriter's own <c>CheckMethodSignaturesMatch</c> requires of a replacement:
        /// the name, the return type, and the parameters by both type AND name.
        /// </summary>
        /// <remarks>
        /// The last two are not pedantry. A replacement that differs from the member it replaces in its
        /// return type or in a parameter name is simply never matched, and an unmatched call site is left
        /// calling the real, blocking BCL method with no error and no warning — the same silent hole as a
        /// replacement that was never written. A gate that ignored either would report full coverage over
        /// a member that is never actually rewritten, which is the one failure it exists to prevent.
        /// </remarks>
        private static bool HasMatchingStatic(Type mock, string name, ParameterInfo[] parameters,
            Type expectedReturnType, bool instanceFirst)
        {
            foreach (MethodInfo candidate in mock.GetMethods(BindingFlags.Public | BindingFlags.Static))
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

        // Compares by NAME rather than identity: the mock is written over its own generic parameter, so the
        // real type's 'T' and the mock's 'T' are distinct Type objects that must still be treated as equal.
        private static bool SameShape(Type left, Type right) =>
            string.Equals(left.Name, right.Name, StringComparison.Ordinal);

        private static string DescribeParameters(ParameterInfo[] parameters)
        {
            var builder = new StringBuilder();
            for (int idx = 0; idx < parameters.Length; ++idx)
            {
                if (idx > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(parameters[idx].ParameterType.Name);
            }

            return builder.ToString();
        }
    }
}
