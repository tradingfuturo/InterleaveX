// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Tools.Tests
{
    /// <summary>
    /// Verifies the source analyzer's project-level invariant against the assemblies that actually run.
    /// </summary>
    public class DeterministicSeedGuardCoverageTests : BaseToolsTest
    {
        private const string EngineType =
            "Microsoft.Coyote.SystematicTesting.TestingEngine";
        private const string GuardType =
            "Microsoft.Coyote.Tests.Common.Architecture.DeterministicSeedIsolationTestsBase";
        private const string SanctionedBuilderType =
            "Microsoft.Coyote.Tests.Common.BaseTest";

        public DeterministicSeedGuardCoverageTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 30000)]
        public void TestEveryCompiledEngineBuilderAssemblyHasAGuard()
        {
            string executingPath = Assembly.GetExecutingAssembly().Location;
            string framework = new DirectoryInfo(Path.GetDirectoryName(executingPath)).Name;
            string configuration = Directory.GetParent(Path.GetDirectoryName(executingPath)).Name;
            string root = FindRepositoryRoot(Path.GetDirectoryName(executingPath));
            var failures = new List<string>();
            var seenModules = new HashSet<Guid>();

            foreach (string projectPath in Directory.GetFiles(
                Path.Combine(root, "Tests"), "*.csproj", SearchOption.AllDirectories))
            {
                var project = XDocument.Load(projectPath);
                string assemblyName = project.Descendants("AssemblyName").Select(node => node.Value)
                    .FirstOrDefault() ?? Path.GetFileNameWithoutExtension(projectPath);
                string assemblyPath = Path.Combine(
                    Path.GetDirectoryName(projectPath), "bin", configuration, framework, assemblyName + ".dll");
                if (!File.Exists(assemblyPath))
                {
                    continue;
                }

                using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
                if (!seenModules.Add(assembly.MainModule.Mvid))
                {
                    continue;
                }

                bool buildsEngine = HasEngineBuilder(assembly);
                bool hasGuard = HasGuard(assembly);
                if (buildsEngine != hasGuard)
                {
                    failures.Add($"{assemblyName}: builds-engine={buildsEngine}, has-guard={hasGuard}");
                }
            }

            Assert.True(failures.Count is 0,
                "compiled test assemblies disagree with the deterministic seed guard convention: " +
                string.Join("; ", failures));
        }

        private static bool HasEngineBuilder(AssemblyDefinition assembly)
        {
            foreach (var type in GetAllTypes(assembly.MainModule.Types))
            {
                if (type.FullName == SanctionedBuilderType)
                {
                    continue;
                }

                foreach (var method in type.Methods.Where(method => method.HasBody))
                {
                    if (method.Body.Instructions.Any(instruction =>
                        instruction.Operand is MethodReference reference &&
                        reference.Name is ".ctor" &&
                        reference.DeclaringType?.FullName == EngineType))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasGuard(AssemblyDefinition assembly) =>
            GetAllTypes(assembly.MainModule.Types).Any(type =>
                !type.IsAbstract && type.BaseType?.FullName == GuardType);

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

        private static string FindRepositoryRoot(string start)
        {
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "InterleaveX.sln")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not find the InterleaveX repository root.");
        }
    }
}
