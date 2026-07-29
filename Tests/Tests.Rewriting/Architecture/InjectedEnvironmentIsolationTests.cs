// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Rewriting.Tests
{
    /// <summary>
    /// Tests which parts of the rewriter may read the .NET installation off the machine.
    /// </summary>
    /// <remarks>
    /// <c>AssemblyInfo.GetDotnetRoot</c> once came in two forms. One was handed a file system and an
    /// environment and answered from them, so that a run describing a different installation got a
    /// different answer; the other read the process and the disk. The second was the easier one to
    /// call -- it took no arguments -- and a caller that held the first's arguments and reached for
    /// it anyway was not a compile error, was not a wrong answer on any developer machine, and was
    /// not visible to any test written against a described installation. It just quietly made that
    /// description not matter.
    ///
    /// That is what happened: the shared framework fallback in the rewriting engine held both an
    /// injected file system and an injected environment and called the parameterless overload, so
    /// which assemblies resolution could fall back to -- and therefore what IL a rewrite produced --
    /// was read off the developer's machine whatever the run was told.
    ///
    /// There is now only the form that has to be told where to look, and the list below is empty
    /// because nothing is left that may read the machine. What this guards is the reintroduction: an
    /// overload taking no arguments is the convenient thing to add the next time a caller has no
    /// file system to hand, and adding one puts every caller back within one keystroke of the bug.
    /// If a caller genuinely holds neither, it belongs in the list, and putting it there should be a
    /// decision somebody made once rather than a default.
    ///
    /// Read off the IL rather than the source, because the two overloads share a name: the
    /// difference is the argument count at the call site, which is exactly what a text search cannot
    /// see and what the compiled call records.
    /// </remarks>
    public class InjectedEnvironmentIsolationTests : BaseRewritingTest
    {
        public InjectedEnvironmentIsolationTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private const string AssemblyInfoType = "Microsoft.Coyote.Rewriting.AssemblyInfo";

        private const string GetDotnetRootMethod = "GetDotnetRoot";

        /// <summary>
        /// The members of 'Microsoft.Coyote.Test.dll' that may read the installation off the machine.
        /// </summary>
        /// <remarks>
        /// None. <c>AddSharedFrameworkDirectories</c> was the last one, on the grounds that it is
        /// reached from a constructor holding no file system and no environment -- true at the time,
        /// and it stopped being true when the incremental cache needed to describe an installation
        /// other than this machine's in order to test which framework version resolution picks. The
        /// pair is threaded through the construction of every <c>AssemblyInfo</c> now.
        /// </remarks>
        private static readonly string[] AllowedToReadTheMachine = Array.Empty<string>();

        [Fact(Timeout = 30000)]
        public void TestOnlyACallerWithNothingToAskReadsTheMachinesInstallation()
        {
            var found = FindHostBoundCallers("Microsoft.Coyote.Test.dll")
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();

            var added = found.Except(AllowedToReadTheMachine, StringComparer.Ordinal).ToArray();
            var removed = AllowedToReadTheMachine.Except(found, StringComparer.Ordinal).ToArray();

            Assert.True(added.Length is 0,
                "these read the .NET installation off the machine rather than off the environment " +
                "the run was given, so a caller describing a different installation is ignored. If " +
                "the method holds a file system and an environment, pass them to the overload that " +
                "takes them; if it genuinely holds neither, add it to the list in this file: " +
                string.Join(", ", added));

            Assert.True(removed.Length is 0,
                "these no longer read the installation off the machine, so the list in this file is " +
                "stale and should have them removed: " + string.Join(", ", removed));
        }

        [Fact(Timeout = 30000)]
        public void TestTheSharedFrameworkFallbackAsksTheEnvironmentItWasGiven()
        {
            // Asserted by name as well as through the frozen list, so that this still fails if
            // someone reacts to the list breaking by pasting the fallback back into it. It is the
            // one place that held an environment and ignored it, and the one whose answer decides
            // what resolution can fall back to.
            Assert.DoesNotContain(
                "Microsoft.Coyote.Rewriting.RewritingEngine::TryResolveFromSharedFrameworks",
                FindHostBoundCallers("Microsoft.Coyote.Test.dll"));
        }

        /// <summary>
        /// Returns every method of the specified assembly that asks for the .NET installation without
        /// naming the file system and environment to answer from.
        /// </summary>
        private static IEnumerable<string> FindHostBoundCallers(string assemblyName)
        {
            string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(directory, assemblyName);
            Assert.True(File.Exists(path), $"could not find '{path}' to inspect");

            var found = new HashSet<string>(StringComparer.Ordinal);
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            bool sawOverload = false;
            foreach (var module in assembly.Modules)
            {
                foreach (var type in GetAllTypes(module.Types))
                {
                    foreach (var method in type.Methods.Where(method => method.HasBody))
                    {
                        foreach (var instruction in method.Body.Instructions)
                        {
                            if (instruction.Operand is MethodReference reference &&
                                reference.Name == GetDotnetRootMethod &&
                                reference.DeclaringType?.FullName == AssemblyInfoType)
                            {
                                sawOverload = true;
                                if (reference.Parameters.Count is 0)
                                {
                                    found.Add($"{type.FullName}::{method.Name}");
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // Otherwise a rename of either overload turns this into a test that inspects an assembly,
            // finds nothing at all, and reports that everything is in order.
            Assert.True(sawOverload,
                $"found no call to '{AssemblyInfoType}::{GetDotnetRootMethod}' in '{assemblyName}', " +
                "so this test no longer looks at anything. It has been renamed or inlined away.");

            return found;
        }

        /// <summary>
        /// Returns the specified types and every type nested inside them.
        /// </summary>
        private static IEnumerable<TypeDefinition> GetAllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (var type in types)
            {
                yield return type;
                foreach (var nested in GetAllTypes(type.NestedTypes))
                {
                    yield return nested;
                }
            }
        }
    }
}
