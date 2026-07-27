// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Coyote.Logging;
using Mono.Cecil;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// Pass that reports thread-static state in the assemblies being rewritten.
    /// </summary>
    /// <remarks>
    /// The runtime reuses a thread across the controlled operations that a test creates, so a value that
    /// one operation writes to a thread-static field can be observed by a later operation that happens to
    /// run on the same thread. This matches how the thread pool behaves outside of testing, but it is not
    /// what a reader of a systematic test expects, and it cannot be prevented: nothing can enumerate or
    /// reset thread-static state that belongs to the program under test. Reporting it is therefore the
    /// remedy, so that a test whose result depends on it is recognized as such instead of being debugged
    /// as a scheduling problem.
    /// </remarks>
    internal sealed class ThreadStaticDetectionPass : AnalysisPass
    {
        /// <summary>
        /// The maximum number of field names to name individually in the report.
        /// </summary>
        private const int MaxReportedFields = 10;

        /// <summary>
        /// The thread-static fields found in the assembly currently being visited.
        /// </summary>
        private readonly List<string> DetectedFields;

        /// <summary>
        /// Initializes a new instance of the <see cref="ThreadStaticDetectionPass"/> class.
        /// </summary>
        internal ThreadStaticDetectionPass(IEnumerable<AssemblyInfo> visitedAssemblies, LogWriter logWriter)
            : base(visitedAssemblies, logWriter)
        {
            this.DetectedFields = new List<string>();
        }

        /// <summary>
        /// The thread-static fields found in the assembly currently being visited.
        /// </summary>
        internal IReadOnlyList<string> ReportedFields => this.DetectedFields;

        /// <inheritdoc/>
        protected internal override void VisitAssembly(AssemblyInfo assembly)
        {
            // A single instance of this pass visits every assembly in turn, so the findings of the
            // previous one must not be reported against this one.
            this.DetectedFields.Clear();
            base.VisitAssembly(assembly);
        }

        /// <inheritdoc/>
        protected internal override void VisitField(FieldDefinition field)
        {
            if (this.IsCompilerGeneratedType || !field.IsStatic)
            {
                return;
            }

            if (field.CustomAttributes.Any(attr => IsTypeOf(attr.AttributeType, typeof(ThreadStaticAttribute))))
            {
                this.DetectedFields.Add($"{GetFullyQualifiedTypeName(field.DeclaringType)}.{field.Name}");
            }
        }

        /// <inheritdoc/>
        protected internal override void CompleteVisit()
        {
            if (this.DetectedFields.Count is 0)
            {
                return;
            }

            var names = this.DetectedFields.Take(MaxReportedFields).ToArray();
            string list = string.Join(", ", names);
            if (this.DetectedFields.Count > names.Length)
            {
                list += $", and {this.DetectedFields.Count - names.Length} more";
            }

            // Reported as important rather than as a warning, because warnings are not shown at the
            // default verbosity and this is the only notice that the behavior below is in play.
            this.LogWriter.LogImportant(
                "..... Found {0} thread-static field(s) in '{1}': {2}. Controlled operations reuse " +
                "threads, so an operation can observe a value that an earlier operation left in one of " +
                "these fields. Pass '--no-thread-pooling', or call " +
                "'Configuration.WithControlledThreadPoolingEnabled(false)', to give each operation its " +
                "own thread instead.",
                this.DetectedFields.Count, this.Assembly?.Name ?? "the assembly", list);
        }
    }
}
