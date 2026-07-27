// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using Microsoft.Coyote.Logging;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// An abstract implementation of a pass that reports on IL without modifying it.
    /// </summary>
    /// <remarks>
    /// The traversal treats such a pass differently in two ways, both of which follow from it modifying
    /// nothing. It also visits types declared with <see cref="SkipRewritingAttribute"/>, because that
    /// attribute exempts a type from being instrumented rather than from having its behavior reported
    /// on. And it does not visit method bodies, so that it does not pay to materialize them from the
    /// image, which matters because an analysis pass runs over every assembly on every build, including
    /// the already rewritten ones that the rewriting passes skip. Expressing this as a base class rather
    /// than as flags on <see cref="Pass"/> is what keeps both exemptions truthful, because a pass that
    /// rewrites IL cannot derive from it and so cannot claim them.
    /// </remarks>
    internal abstract class AnalysisPass : Pass
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AnalysisPass"/> class.
        /// </summary>
        protected AnalysisPass(IEnumerable<AssemblyInfo> visitedAssemblies, LogWriter logWriter)
            : base(visitedAssemblies, logWriter)
        {
        }

        /// <inheritdoc/>
        protected internal sealed override bool VisitsSkippedTypes => true;

        /// <inheritdoc/>
        protected internal sealed override bool VisitsMethodBodies => false;
    }
}
