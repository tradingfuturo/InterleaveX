// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Rewriting.Tests
{
    /// <summary>
    /// Tests which parts of the product are allowed to reach the file system of the machine.
    /// </summary>
    /// <remarks>
    /// The decisions that skip a rewrite, and the copy that those decisions protect, were separated
    /// from the file system so that they could be tested against one held in memory. Nothing stops
    /// the next change from reaching for <c>File.Exists</c> again, and nothing would notice: the
    /// tests written against the injected file system would keep passing while quietly no longer
    /// describing what the code does.
    ///
    /// So the members that legitimately touch the file system are frozen below, and any other one is
    /// a failure. The list is meant to be edited -- a new entry is a deliberate decision, made once,
    /// by someone who read this -- rather than to never change.
    /// </remarks>
    public class HostFileSystemIsolationTests : BaseRewritingTest
    {
        public HostFileSystemIsolationTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// The types through which the file system of the machine is reached.
        /// </summary>
        /// <remarks>
        /// <see cref="FileInfo"/> and <see cref="DirectoryInfo"/> are here, and their base type with
        /// them, because that is how the rewriting cache used to read the file system: a scan for
        /// <c>File</c> and <c>Directory</c> alone would have declared it clean while it was calling
        /// <c>new DirectoryInfo(path).GetFiles("*.dll")</c> and reading <c>Length</c> off the result.
        ///
        /// <see cref="Path"/> is deliberately absent. Its members are pure string functions with no
        /// file system behind them.
        /// </remarks>
        private static readonly HashSet<string> HostFileSystemTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.IO.File",
            "System.IO.Directory",
            "System.IO.FileInfo",
            "System.IO.DirectoryInfo",
            "System.IO.FileSystemInfo",
            "System.IO.FileStream",
            "System.IO.StreamReader",
            "System.IO.StreamWriter"
        };

        /// <summary>
        /// The members of 'Microsoft.Coyote.dll' and 'Microsoft.Coyote.Actors.dll' that reach the
        /// file system.
        /// </summary>
        /// <remarks>
        /// The runtime that ships to every user writes coverage reports and nothing else. Adding to
        /// this list means adding file system access to the assembly that is loaded into every
        /// process running a Coyote test.
        /// </remarks>
        private static readonly string[] AllowedInRuntime = new[]
        {
            "Microsoft.Coyote.Coverage.CoverageGraph::SaveDgml",
            "Microsoft.Coyote.Coverage.CoverageInfo::Load",
            "Microsoft.Coyote.Coverage.CoverageInfo::Save",
            "Microsoft.Coyote.Coverage.CoverageReporter::TryEmitActivityCoverageReport",
            "Microsoft.Coyote.Coverage.CoverageReporter::TryEmitScheduleCoverageReport"
        };

        /// <summary>
        /// The types that must never reach the file system directly, whatever else does.
        /// </summary>
        /// <remarks>
        /// These are the ones whose tests would otherwise stop meaning anything: each is written
        /// against an injected file system, so a direct call from inside one of them is behaviour no
        /// test can see and no test can set up.
        /// </remarks>
        private static readonly string[] MustBeFileSystemFree = new[]
        {
            "Microsoft.Coyote.Rewriting.RewritingCache",
            "Microsoft.Coyote.Rewriting.RewritingCacheValidator",
            "Microsoft.Coyote.Rewriting.RewritingCacheExpectation",
            "Microsoft.Coyote.Rewriting.RewritingOutputMirror",
            "Microsoft.Coyote.Rewriting.RewritingOutputLedger",
            "Microsoft.Coyote.IO.FileSystemPathComparer",
            "Microsoft.Coyote.Rewriting.CacheManifest",
            "Microsoft.Coyote.Rewriting.CacheEntry",
            "Microsoft.Coyote.Rewriting.CacheFile",
            "Microsoft.Coyote.Rewriting.CacheDirectory"
        };

        /// <summary>
        /// The answer for each assembly already inspected.
        /// </summary>
        /// <remarks>
        /// Finding them means reading the assembly and walking every instruction of every method
        /// body, which is what the generous timeouts below are for, and the tests here ask about
        /// 'Microsoft.Coyote.Test.dll' twice for the very same set. Concurrent because xunit is free
        /// to run two test classes at once, and this is static.
        /// </remarks>
        private static readonly ConcurrentDictionary<string, string[]> InspectedAssemblies =
            new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);

        [Fact(Timeout = 30000)]
        public void TestTheRuntimeOnlyReachesTheFileSystemToWriteCoverage()
        {
            var found = GetHostFileSystemUsers("Microsoft.Coyote.dll")
                .Concat(GetHostFileSystemUsers("Microsoft.Coyote.Actors.dll"))
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();

            AssertExactly(AllowedInRuntime, found, "the runtime assemblies");
        }

        [Fact(Timeout = 30000)]
        public void TestTheCacheAndTheMirrorNeverReachTheFileSystem()
        {
            // Narrower than the frozen list above and worth stating separately: this is the claim
            // their tests rest on, so it should fail with a message about them rather than about a
            // list of members somewhere else having changed.
            var offenders = GetHostFileSystemUsers("Microsoft.Coyote.Test.dll")
                .Where(name => MustBeFileSystemFree.Any(type =>
                    name.StartsWith(type + "::", StringComparison.Ordinal) ||
                    name.StartsWith(type + "/", StringComparison.Ordinal)))
                .ToArray();

            Assert.True(offenders.Length is 0,
                "these are tested against a file system supplied to them, so reaching the real one " +
                "is behaviour their tests cannot see: " + string.Join(", ", offenders));
        }

        [Fact(Timeout = 30000)]
        public void TestEveryOtherFileSystemUserIsOneWeKnowAbout()
        {
            // The rewriting engine, the assembly loader and the parallel test plumbing all reach the
            // file system for good reasons, and Mono.Cecil reaches it whatever anyone here does. What
            // this asks is only that the set stops growing by accident.
            AssertExactly(AllowedInTestAssembly, GetHostFileSystemUsers("Microsoft.Coyote.Test.dll"),
                "'Microsoft.Coyote.Test.dll'");
        }

        [Fact(Timeout = 30000)]
        public void TestNoFrozenNameIsCompilerGenerated()
        {
            // A generated name is not a name anybody chose: it changes when a lambda is added beside
            // it, when one is removed, and when the compiler changes how it spells them, and each of
            // those fails these tests over something that is not a file system access at all. One was
            // frozen in here -- 'ParallelWorkerStopProbe::<Create>b__0' -- because 'Describe' could
            // recover the method name from an iterator's type but not from a closure's, and the raw
            // name it fell back to looked plausible enough to paste into the list.
            foreach (string name in AllowedInRuntime.Concat(AllowedInTestAssembly))
            {
                Assert.True(name.IndexOfAny(new[] { '<', '>' }) < 0,
                    $"'{name}' is a compiler-generated name rather than one somebody wrote. It should " +
                    "be reported under the method it came from; if it is not, 'Describe' cannot " +
                    "normalize it and freezing it here makes this test fail on the next edit nearby.");
            }
        }

        [Fact(Timeout = 30000)]
        public void TestALambdaIsReportedUnderTheMethodItCameFrom()
        {
            // 'ParallelWorkerStopProbe.Create' is the one place in the product where a lambda reaches
            // the file system, so it is what says whether the normalization works. Asserted directly
            // rather than only through the frozen list, so that this still fails if someone reacts to
            // the list breaking by pasting the generated name back into it.
            var found = GetHostFileSystemUsers("Microsoft.Coyote.Test.dll");
            const string Probe = "Microsoft.Coyote.SystematicTesting.ParallelWorkerStopProbe";

            Assert.Contains(Probe + "::Create", found);
            Assert.DoesNotContain(found, name =>
                name.StartsWith(Probe + "::<", StringComparison.Ordinal));
        }

        /// <summary>
        /// Returns every method of the specified assembly that reaches the file system of the machine,
        /// in a stable order, inspecting it only the first time it is asked about.
        /// </summary>
        private static string[] GetHostFileSystemUsers(string assemblyName) =>
            InspectedAssemblies.GetOrAdd(assemblyName, name => FindHostFileSystemUsers(name)
                .OrderBy(user => user, StringComparer.Ordinal).ToArray());

        /// <summary>
        /// Fails with what was added and what went away, rather than with two long lists.
        /// </summary>
        private static void AssertExactly(string[] expected, string[] actual, string what)
        {
            var added = actual.Except(expected, StringComparer.Ordinal).ToArray();
            var removed = expected.Except(actual, StringComparer.Ordinal).ToArray();

            Assert.True(added.Length is 0,
                $"new file system access in {what}. If this is deliberate, add it to the list in this " +
                "file; if it is not, route it through 'IFileSystem': " + string.Join(", ", added));

            Assert.True(removed.Length is 0,
                $"these no longer reach the file system in {what}, so the list in this file is stale " +
                "and should have them removed: " + string.Join(", ", removed));
        }

        /// <summary>
        /// Returns every method of the specified assembly that reaches the file system of the machine.
        /// </summary>
        /// <remarks>
        /// Read off the IL rather than the source, so that it sees a call however it was spelled, and
        /// sees constructors and property getters as readily as ordinary calls. Compiler generated
        /// types are reported under the method they came from where that can be recovered, so that a
        /// lambda reaching for the file system is not filed under an unnameable closure.
        /// </remarks>
        private static IEnumerable<string> FindHostFileSystemUsers(string assemblyName)
        {
            string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(directory, assemblyName);
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
                            if (instruction.Operand is MemberReference member &&
                                member.DeclaringType != null &&
                                HostFileSystemTypes.Contains(member.DeclaringType.FullName))
                            {
                                found.Add(Describe(type, method));
                                break;
                            }
                        }
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Returns the name to report the specified method under.
        /// </summary>
        private static string Describe(TypeDefinition type, MethodDefinition method)
        {
            // A lambda, a local function or an iterator lives in a generated type, under a name that
            // is unreadable and changes with the compiler. Reporting the enclosing type and the name
            // of the method it came from keeps this stable and says where to look.
            var declaring = type;
            while (declaring.DeclaringType != null && IsCompilerGenerated(declaring))
            {
                declaring = declaring.DeclaringType;
            }

            // The method it came from is embedded in whichever of the two names the compiler chose to
            // decorate, and which one that is depends on the construct: a lambda has it in the method
            // ('<Create>b__0') while its type says nothing ('<>c__DisplayClass0_0'), and an iterator
            // has it in the type ('<Create>d__3') while its method is just 'MoveNext'. The method is
            // tried first because it is the more specific of the two -- a lambda inside an iterator
            // names the lambda.
            string name = ExtractName(method.Name) ??
                (ReferenceEquals(declaring, type) ? null : ExtractName(type.Name)) ??
                method.Name;

            return $"{declaring.FullName}::{name}";
        }

        /// <summary>
        /// Returns the method name a compiler-generated name was built from, or null if there is none
        /// in it.
        /// </summary>
        private static string ExtractName(string generated)
        {
            int start = generated.IndexOf('<');
            int end = generated.IndexOf('>');
            return start >= 0 && end > start + 1 ? generated.Substring(start + 1, end - start - 1) : null;
        }

        /// <summary>
        /// Returns true if the specified type was emitted by the compiler rather than written.
        /// </summary>
        private static bool IsCompilerGenerated(TypeDefinition type) =>
            type.Name.StartsWith("<", StringComparison.Ordinal) ||
            type.CustomAttributes.Any(attribute => attribute.AttributeType.Name ==
                "CompilerGeneratedAttribute");

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

        /// <summary>
        /// The members of 'Microsoft.Coyote.Test.dll' that reach the file system.
        /// </summary>
        /// <remarks>
        /// Four groups, and it is worth knowing which is which before adding to it.
        ///
        /// <see cref="Microsoft.Coyote.IO.HostFileSystem"/> is the one place that is supposed to be
        /// here: it is the real implementation of the seam, so every one of its members is a file
        /// system call by definition.
        ///
        /// The two <c>RewritingEngine</c> members left here move the assemblies themselves, and what
        /// keeps them here is narrower than it once was. It is not that Mono.Cecil is involved: it is
        /// that these two act on bytes Cecil has already written to a real path. The copy that puts a
        /// rewritten assembly in its final place reads what <c>assembly.Write</c> just produced, and
        /// the probe beside it asks about the symbol file that same write emitted. A file system that
        /// answered either of those from anywhere but the disk would be describing something that
        /// does not exist, and the mismatch would be silent -- so they are honest about reaching the
        /// real one rather than taking a seam they cannot mean.
        ///
        /// Everything else is on the seam, including all of <c>AssemblyInfo</c>: which framework
        /// version wins, what a runtime configuration asks for, whether a dependency sits beside its
        /// referrer. Those are decisions rather than transfers, and each is now something a test can
        /// set up -- which is what the incremental cache needs, since a candidate it never looked at
        /// is exactly what used to change underneath it unnoticed.
        ///
        /// <c>ParallelTestFiles</c> is here on purpose too. Its tests are written against the real
        /// file system because what they check is that it never throws, whatever the file system
        /// does, and a fake would only reproduce the exceptions someone chose to give it.
        /// <c>RewritingOutputLock</c> likewise exercises host file-handle exclusion itself; routing
        /// it through the transactional content seam would no longer test or provide that exclusion.
        ///
        /// The rest -- report and artifact writing, telemetry, options parsing -- simply has not
        /// needed a seam yet. Any of them can get one when something wants to test it.
        /// </remarks>
        private static readonly string[] AllowedInTestAssembly = new[]
        {
            "Microsoft.Coyote.IO.HostFileSystem/HostFileEntry::get_Exists",
            "Microsoft.Coyote.IO.HostFileSystem/HostFileEntry::get_LastWriteTimeUtc",
            "Microsoft.Coyote.IO.HostFileSystem/HostFileEntry::get_Length",
            "Microsoft.Coyote.IO.HostFileSystem/HostFileEntry::get_Path",
            "Microsoft.Coyote.IO.HostFileSystem::CopyFile",
            "Microsoft.Coyote.IO.HostFileSystem::CreateDirectory",
            "Microsoft.Coyote.IO.HostFileSystem::DeleteDirectory",
            "Microsoft.Coyote.IO.HostFileSystem::DeleteFile",
            "Microsoft.Coyote.IO.HostFileSystem::DirectoryExists",
            "Microsoft.Coyote.IO.HostFileSystem::FileExists",
            "Microsoft.Coyote.IO.HostFileSystem::GetDirectories",
            "Microsoft.Coyote.IO.HostFileSystem::GetFile",
            "Microsoft.Coyote.IO.HostFileSystem::GetFileEntries",
            "Microsoft.Coyote.IO.HostFileSystem::GetFiles",
            "Microsoft.Coyote.IO.HostFileSystem::IsCaseInsensitive",
            "Microsoft.Coyote.IO.HostFileSystem::MoveFile",
            "Microsoft.Coyote.IO.HostFileSystem::OpenRead",
            "Microsoft.Coyote.IO.HostFileSystem::QueryOrProbe",
            "Microsoft.Coyote.IO.HostFileSystem::QueryOrProbeForTesting",
            "Microsoft.Coyote.IO.HostFileSystem::ReadAllText",
            "Microsoft.Coyote.IO.HostFileSystem::ReplaceFile",
            "Microsoft.Coyote.IO.HostFileSystem::WriteAllText",
            "Microsoft.Coyote.Rewriting.RewritingEngine::CopyWithRetriesAsync",
            "Microsoft.Coyote.Rewriting.RewritingEngine::RewriteAssembly",
            "Microsoft.Coyote.Rewriting.RewritingOptions::ParseFromJSON",
            "Microsoft.Coyote.Rewriting.RewritingOptions::Sanitize",
            "Microsoft.Coyote.Rewriting.RewritingOutputLock::Acquire",
            "Microsoft.Coyote.Rewriting.RewritingOutputLock::ReadOwner",
            "Microsoft.Coyote.SystematicTesting.ParallelTestFiles::TryCreate",
            "Microsoft.Coyote.SystematicTesting.ParallelTestFiles::TryDelete",
            "Microsoft.Coyote.SystematicTesting.ParallelTestFiles::TryDeleteDirectory",
            "Microsoft.Coyote.SystematicTesting.ParallelWorkerStopProbe::Create",
            "Microsoft.Coyote.SystematicTesting.TestMethodInfo::OnResolving",
            "Microsoft.Coyote.SystematicTesting.TestReport::Load",
            "Microsoft.Coyote.SystematicTesting.TestReport::Save",
            "Microsoft.Coyote.SystematicTesting.TestingEngine::TryEmitCoverageReports",
            "Microsoft.Coyote.SystematicTesting.TestingEngine::TryEmitReports",
            "Microsoft.Coyote.Telemetry.TelemetryClient::GetOrCreateDeviceId"
        };
    }
}
