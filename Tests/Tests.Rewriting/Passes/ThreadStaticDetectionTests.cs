// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Coyote.Logging;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Rewriting.Tests
{
    /// <summary>
    /// Tests that rewriting reports the thread-static state it finds.
    /// </summary>
    public class ThreadStaticDetectionTests : BaseRewritingTest
    {
        public ThreadStaticDetectionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        // The fields below exist so that the pass has real metadata to read, and are deliberately never
        // assigned, because the pass reads how they are declared rather than what they hold.
#pragma warning disable CS0649
        /// <summary>
        /// Sample state that the pass is expected to report.
        /// </summary>
        [ThreadStatic]
        private static int DetectableThreadStaticField;

        /// <summary>
        /// Sample state that the pass is expected to ignore.
        /// </summary>
        private static int PlainStaticField;

        /// <summary>
        /// Sample state in a type that has opted out of being rewritten, which the pass is expected to
        /// report all the same.
        /// </summary>
        [SkipRewriting("Sample for checking that reporting is not confined to the rewritten types.")]
        internal static class SkippedType
        {
            [ThreadStatic]
            internal static int SkippedThreadStaticField;
        }
#pragma warning restore CS0649

        [Fact(Timeout = 30000)]
        public void TestThreadStaticFieldInSkippedTypeIsReported()
        {
            (var pass, var logWriter) = CreatePass();
            try
            {
                // Driven through the real traversal rather than by visiting the type directly, because
                // what is being checked is that the traversal hands the type over at all.
                using (AssemblyInfo assembly = LoadThisAssembly())
                {
                    assembly.Invoke(pass);
                }

                Assert.Contains(pass.ReportedFields, name => name.EndsWith(
                    nameof(SkippedType.SkippedThreadStaticField), StringComparison.Ordinal));
            }
            finally
            {
                logWriter.Dispose();
            }
        }

        [Fact(Timeout = 30000)]
        public void TestSkippedTypesStayHiddenFromRewritingPasses()
        {
            MemoryLogWriter logWriter = CreateLogWriter();
            try
            {
                var rewriter = new TypeRecordingPass(false, logWriter);
                var reporter = new TypeRecordingPass(true, logWriter);
                using (AssemblyInfo assembly = LoadThisAssembly())
                {
                    assembly.Invoke(rewriter);
                    assembly.Invoke(reporter);
                }

                // Cecil names a nested type with a slash rather than the plus that reflection uses.
                string skippedType = typeof(SkippedType).FullName.Replace('+', '/');
                Assert.DoesNotContain(skippedType, rewriter.VisitedTypes);
                Assert.Contains(skippedType, reporter.VisitedTypes);
            }
            finally
            {
                logWriter.Dispose();
            }
        }

        [Fact(Timeout = 5000)]
        public void TestThreadStaticFieldIsReported()
        {
            (var pass, var logWriter) = CreatePass();
            try
            {
                using AssemblyInfo assembly = LoadThisAssembly();
                TypeDefinition type = ResolveThisType(assembly);
                pass.VisitAssembly(null);
                pass.VisitType(type);
                foreach (var field in type.Fields)
                {
                    pass.VisitField(field);
                }

                Assert.Contains(pass.ReportedFields,
                    name => name.EndsWith(nameof(DetectableThreadStaticField), StringComparison.Ordinal));
                Assert.DoesNotContain(pass.ReportedFields,
                    name => name.EndsWith(nameof(PlainStaticField), StringComparison.Ordinal));

                // The report has to be visible at the verbosity a build actually uses, so it is written
                // as an important message rather than as a warning.
                pass.CompleteVisit();
                Assert.Contains("thread-static field", logWriter.GetObservedMessages(), StringComparison.Ordinal);
                Assert.Contains("--no-thread-pooling", logWriter.GetObservedMessages(), StringComparison.Ordinal);
            }
            finally
            {
                logWriter.Dispose();
            }
        }

        [Fact(Timeout = 5000)]
        public void TestReportIsResetForEachAssembly()
        {
            (var pass, var logWriter) = CreatePass();
            try
            {
                using AssemblyInfo assembly = LoadThisAssembly();
                TypeDefinition type = ResolveThisType(assembly);
                pass.VisitAssembly(null);
                pass.VisitType(type);
                foreach (var field in type.Fields)
                {
                    pass.VisitField(field);
                }

                Assert.NotEmpty(pass.ReportedFields);

                // One instance of the pass visits every assembly in turn, so what it found in the
                // previous one must not be reported against the next.
                pass.VisitAssembly(null);
                Assert.Empty(pass.ReportedFields);
            }
            finally
            {
                logWriter.Dispose();
            }
        }

        /// <summary>
        /// Creates the pass along with the log writer that captures what it reports.
        /// </summary>
        private static (ThreadStaticDetectionPass Pass, MemoryLogWriter LogWriter) CreatePass()
        {
            MemoryLogWriter logWriter = CreateLogWriter();
            return (new ThreadStaticDetectionPass(Array.Empty<AssemblyInfo>(), logWriter), logWriter);
        }

        /// <summary>
        /// Creates a log writer that captures what a pass reports, at the verbosity a build uses.
        /// </summary>
        private static MemoryLogWriter CreateLogWriter() =>
            new MemoryLogWriter(Coyote.Configuration.Create().WithVerbosityEnabled(VerbosityLevel.Info));

        /// <summary>
        /// Loads this test assembly from disk, so that a pass can be driven over it the same way the
        /// rewriting engine drives one.
        /// </summary>
        /// <remarks>
        /// The constructor is reached by reflection rather than through the loading helper the engine
        /// uses, because that helper also validates the assembly, and this one has already been rewritten
        /// as part of the build with options that no test could restate.
        /// </remarks>
        private static AssemblyInfo LoadThisAssembly()
        {
            string path = typeof(ThreadStaticDetectionTests).Assembly.Location;
            RewritingOptions options = RewritingOptions.Create();
            options.AssembliesDirectory = Path.GetDirectoryName(path);

            var constructor = typeof(AssemblyInfo).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[]
                {
                    typeof(string), typeof(string), typeof(RewritingOptions), typeof(AssemblyResolveEventHandler)
                },
                null);
            Assert.NotNull(constructor);
            return (AssemblyInfo)constructor.Invoke(
                new object[] { Path.GetFileName(path), path, options, null });
        }

        /// <summary>
        /// A pass that records the types the traversal gives it.
        /// </summary>
        private sealed class TypeRecordingPass : Pass
        {
            /// <summary>
            /// True if this pass declares that it visits types that opted out of rewriting.
            /// </summary>
            private readonly bool VisitsSkipped;

            /// <summary>
            /// Initializes a new instance of the <see cref="TypeRecordingPass"/> class.
            /// </summary>
            internal TypeRecordingPass(bool visitsSkipped, LogWriter logWriter)
                : base(Array.Empty<AssemblyInfo>(), logWriter)
            {
                this.VisitsSkipped = visitsSkipped;
                this.VisitedTypes = new List<string>();
            }

            /// <summary>
            /// The names of the types this pass was given.
            /// </summary>
            internal List<string> VisitedTypes { get; }

            /// <inheritdoc/>
            protected internal override bool VisitsSkippedTypes => this.VisitsSkipped;

            /// <inheritdoc/>
            protected internal override void VisitType(TypeDefinition type)
            {
                base.VisitType(type);
                this.VisitedTypes.Add(type.FullName);
            }

            /// <inheritdoc/>
            protected internal override void VisitMethod(MethodDefinition method)
            {
            }
        }

        /// <summary>
        /// Resolves this test type from the specified assembly, so that the pass sees real metadata.
        /// </summary>
        private static TypeDefinition ResolveThisType(AssemblyInfo assembly)
        {
            TypeDefinition type = assembly.Definition.MainModule.GetType(
                typeof(ThreadStaticDetectionTests).FullName);
            Assert.NotNull(type);
            return type;
        }
    }
}
