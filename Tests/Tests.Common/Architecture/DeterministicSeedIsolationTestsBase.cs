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

namespace Microsoft.Coyote.Tests.Common.Architecture
{
    /// <summary>
    /// Asserts that every test in one assembly explores from a seed it can be re-run with.
    /// </summary>
    /// <remarks>
    /// Each test derives a starting seed from its own name, so it explores the same schedules on
    /// every run and a failure can be reproduced by running it again. That is applied where the base
    /// class builds the engine, which covers every test that goes through it and no test that builds
    /// its own. Such a test keeps working and keeps passing; what it stops doing is being
    /// reproducible, and nothing says so until a nightly failure cannot be re-run.
    ///
    /// One had already slipped through, so the methods that build an engine themselves are frozen by
    /// each assembly that has any, and a new one is a failure. Building one means constructing it or
    /// asking the type for one -- <c>TestingEngine.Create</c> and anything else static on it that
    /// hands one back -- because the second reaches the same constructor through a body that lives
    /// in the product assembly, where no scan of a test assembly can see it.
    ///
    /// Stated here and derived from once per assembly, rather than written out in each, because a
    /// test can only read the assemblies beside it: a test project's output directory holds its own
    /// assembly and nothing of its siblings, so there is no one place from which all of them are
    /// visible. A Roslyn analyzer closes that project-level gap while compiling the source, and the
    /// centralized Cecil coverage test verifies the same invariant over the built assemblies.
    ///
    /// What this deliberately does not check is that each frozen method is in fact seeded. The seed
    /// is usually named by the configuration factory a builder calls rather than by the builder
    /// itself, so reading the IL of the method alone would report every one of them as unseeded. The
    /// list is the guard: adding to it is a deliberate decision, made once, by someone who checked.
    /// </remarks>
    public abstract class DeterministicSeedIsolationTestsBase
    {
        /// <summary>
        /// The file name of the assembly this checks, which is the one it sits in.
        /// </summary>
        protected abstract string AssemblyFileName { get; }

        /// <summary>
        /// The methods of that assembly that build a testing engine rather than letting the base
        /// class do it, each with a note saying how it is seeded.
        /// </summary>
        protected abstract IReadOnlyList<string> AllowedToBuildAnEngine { get; }

        [Fact(Timeout = 30000)]
        public void TestEveryEngineBuiltHereIsSeededDeliberately()
        {
            var found = FindEngineBuilders(this.AssemblyFileName)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();

            var added = found.Except(this.AllowedToBuildAnEngine, StringComparer.Ordinal).ToArray();
            var removed = this.AllowedToBuildAnEngine.Except(found, StringComparer.Ordinal).ToArray();

            Assert.True(added.Length is 0,
                "these build a testing engine without going through the base class, which is where " +
                "the per-test seed is applied, so they explore a different schedule on every run and " +
                "a failure cannot be reproduced. Either run the test through the base class, or pass " +
                "the configuration through 'WithDefaultRandomSeed' (or pin a seed deliberately) and " +
                "add it to the list in this file: " + string.Join(", ", added));

            Assert.True(removed.Length is 0,
                "these no longer build a testing engine of their own, so the list in this file is " +
                "stale and should have them removed: " + string.Join(", ", removed));
        }

        /// <summary>
        /// Returns the methods of the specified assembly that build a testing engine.
        /// </summary>
        /// <remarks>
        /// Read off the IL rather than the source, so that it sees the construction however it was
        /// spelled and wherever it was moved to.
        /// </remarks>
        private static IEnumerable<string> FindEngineBuilders(string assemblyFileName)
        {
            string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(directory, assemblyFileName);
            Assert.True(File.Exists(path), $"could not find '{path}' to inspect");

            var found = new HashSet<string>(StringComparer.Ordinal);
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            foreach (var module in assembly.Modules)
            {
                foreach (var type in GetAllTypes(module.Types))
                {
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody)
                        {
                            continue;
                        }

                        foreach (var instruction in method.Body.Instructions)
                        {
                            if (EngineBuilderScan.IsEngineBuild(instruction.Operand))
                            {
                                found.Add($"{type.FullName}::{method.Name}");
                                break;
                            }
                        }
                    }
                }
            }

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
