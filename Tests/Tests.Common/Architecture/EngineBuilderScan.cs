// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using Mono.Cecil;

namespace Microsoft.Coyote.Tests.Common.Architecture
{
    /// <summary>
    /// What it looks like, in compiled IL, for a method to build a testing engine.
    /// </summary>
    /// <remarks>
    /// Stated once and used by both scans -- the per-assembly guard that lists the methods, and the
    /// centralized coverage test that asks each assembly the yes-or-no question -- because the two
    /// answering differently is a hole rather than a disagreement: the coverage test would report an
    /// assembly as needing a guard that the guard itself then reports as having nothing to freeze,
    /// and no edit to either file alone would fix it.
    ///
    /// The <c>DeterministicSeedGuardAnalyzer</c> in 'Tools/TestConventionAnalyzer' asks the
    /// same question of the source while it compiles. It cannot share this code -- it runs against
    /// Roslyn symbols in a netstandard2.0 assembly with no reference to Cecil or to this project --
    /// so its rule is written out again there, and the tests in
    /// 'Tests.Tools/Architecture/DeterministicSeedGuardAnalyzerTests.cs' are what keep the two
    /// spellings of it saying the same thing.
    /// </remarks>
    public static class EngineBuilderScan
    {
        /// <summary>
        /// The full name of the testing engine type, as it is spelled in a metadata reference.
        /// </summary>
        public const string EngineTypeName = "Microsoft.Coyote.SystematicTesting.TestingEngine";

        /// <summary>
        /// Returns true if the specified instruction operand builds a testing engine.
        /// </summary>
        /// <remarks>
        /// Two shapes, and the second is the one a scan for construction misses entirely. Calling
        /// the constructor is the obvious one. Asking the type for an instance --
        /// <c>TestingEngine.Create</c> and anything else static on it that hands one back -- also
        /// reaches that constructor, but through a body that lives in the product assembly, so a
        /// test assembly calling it holds no construction for anybody to find.
        ///
        /// The factory is matched by what it returns rather than by its name, so a second one added
        /// beside <c>Create</c> is covered without anyone remembering this file exists. It has to be
        /// declared on the engine: a static helper elsewhere that returns one is itself a method
        /// this reports, and matching it here would report every one of its callers as well.
        /// </remarks>
        public static bool IsEngineBuild(object operand) =>
            operand is MethodReference reference &&
            reference.DeclaringType?.FullName == EngineTypeName &&
            (reference.Name is ".ctor" || reference.ReturnType?.FullName == EngineTypeName);
    }
}
