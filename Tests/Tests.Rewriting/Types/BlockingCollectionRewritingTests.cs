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

            // Constructors are rewritten to a static 'Create' whose parameters match the constructor's.
            foreach (ConstructorInfo ctor in real.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!HasMatchingStatic(mock, "Create", ctor.GetParameters(), instanceFirst: false))
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

                if (!HasMatchingStatic(mock, method.Name, method.GetParameters(), instanceFirst: true))
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

                if (!HasMatchingStatic(mock, method.Name, method.GetParameters(), instanceFirst: false))
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

        private static bool HasMatchingStatic(Type mock, string name, ParameterInfo[] parameters, bool instanceFirst)
        {
            foreach (MethodInfo candidate in mock.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
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
                    if (!SameShape(candidateParameters[idx + offset].ParameterType, parameters[idx].ParameterType))
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
