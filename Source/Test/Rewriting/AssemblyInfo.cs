// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Coyote.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// Contains information for an assembly that is being rewritten.
    /// </summary>
    internal sealed class AssemblyInfo : IDisposable
    {
        /// <summary>
        /// The full name of the assembly.
        /// </summary>
        internal readonly string FullName;

        /// <summary>
        /// The name of the assembly.
        /// </summary>
        internal readonly string Name;

        /// <summary>
        /// The path to the assembly file.
        /// </summary>
        internal readonly string FilePath;

        /// <summary>
        /// The assembly definition.
        /// </summary>
        internal readonly AssemblyDefinition Definition;

        /// <summary>
        /// The assembly direct dependencies.
        /// </summary>
        private readonly HashSet<AssemblyInfo> Dependencies;

        /// <summary>
        /// The names of the assemblies that this assembly directly references.
        /// </summary>
        /// <remarks>
        /// Captured while the definition is open, because <see cref="Dispose"/> runs before the
        /// rewriting cache records the assembly.
        /// </remarks>
        internal readonly IReadOnlyList<string> ReferenceNames;

        /// <summary>
        /// The resolver of this assembly.
        /// </summary>
        private readonly TrackingAssemblyResolver Resolver;

        /// <summary>
        /// The paths of every module that was resolved while visiting this assembly.
        /// </summary>
        /// <remarks>
        /// What the rewriting passes emit depends on the assemblies they resolve, and those reach well
        /// beyond the dependencies collected by <see cref="LoadDependencies"/>, which only records
        /// sibling files that participate in the rewrite. The rewriting cache needs the set that was
        /// actually consulted, otherwise a changed reference could alter the emitted IL while every
        /// file the cache knows about stayed the same.
        /// </remarks>
        internal IEnumerable<string> ResolvedModulePaths => this.Resolver.ResolvedModulePaths;

        /// <summary>
        /// The directories that were searched while resolving the modules of this assembly.
        /// </summary>
        /// <remarks>
        /// The resolved modules say what was read; these say where something else could have been read
        /// from instead. An assembly appearing in any of them can win a resolution that previously went
        /// elsewhere, or satisfy one that previously failed, without any file the last run read being
        /// touched. That covers more than the configured search paths: a newly installed framework
        /// patch adds a shared framework directory, and the rewriter's own directory can gain an
        /// assembly without the rewriter itself being rebuilt.
        /// </remarks>
        internal readonly IReadOnlyList<string> SearchDirectories;

        /// <summary>
        /// The directories holding every installed version of each shared framework this assembly
        /// asked for.
        /// </summary>
        /// <remarks>
        /// <see cref="SearchDirectories"/> holds the one version directory that
        /// <see cref="ResolveFrameworkDirectory"/> picked, which says nothing about the versions it
        /// picked between. Installing a newer patch of the same major adds a directory beside the
        /// chosen one and changes what a fresh run would resolve against, while every directory and
        /// every file the last run touched stays exactly as it was. So the parent is recorded too,
        /// by the names it offers rather than by their content: what matters here is which candidates
        /// exist, and the winner's content is already covered as a search directory.
        /// </remarks>
        internal readonly IReadOnlyList<string> FrameworkInventoryRoots;

        /// <summary>
        /// The rewriting options.
        /// </summary>
        private readonly RewritingOptions Options;

        /// <summary>
        /// The file system this assembly is read through.
        /// </summary>
        private readonly IFileSystem FileSystem;

        /// <summary>
        /// Reads the environment this assembly resolves its frameworks against.
        /// </summary>
        private readonly Func<string, string> GetEnvironmentVariable;

        /// <summary>
        /// True if the assembly has been rewritten, else false.
        /// </summary>
        internal bool IsRewritten { get; private set; }

        /// <summary>
        /// True if the assembly has been disposed, else false.
        /// </summary>
        private bool IsDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="AssemblyInfo"/> class.
        /// </summary>
        private AssemblyInfo(string name, string path, RewritingOptions options, AssemblyResolveEventHandler handler,
            IFileSystem fileSystem, Func<string, string> getEnvironmentVariable)
        {
            this.Name = name;
            this.FilePath = path;
            this.Dependencies = new HashSet<AssemblyInfo>();
            this.Options = options;
            this.FileSystem = fileSystem;
            this.GetEnvironmentVariable = getEnvironmentVariable;
            this.IsRewritten = false;
            this.IsDisposed = false;

            // TODO: can we reuse it, or do we need a new one for each assembly?
            var assemblyResolver = new TrackingAssemblyResolver();

            // Add known search directories for resolving assemblies. The directory of the assemblies
            // being rewritten is searched first, so that shared dependencies -- most importantly the
            // Microsoft.Coyote runtime assemblies -- resolve to the copies that match the target's
            // framework, rather than the copies shipped alongside the rewriter, which can target a
            // different framework. Resolving against a mismatched framework would otherwise bake that
            // framework's core-library references into the rewritten assembly (for example, the net10
            // rewriter injecting net10 'System.Private.CoreLib'/'System.Runtime' references into a net8
            // assembly), making the rewritten assembly fail to load on the target's runtime.
            assemblyResolver.AddSearchDirectory(this.Options.AssembliesDirectory);

            // Explicitly configured search paths come next, ahead of the rewriter's own directory, for
            // the same reason: they describe the target's framework, whereas the rewriter's directory
            // describes the rewriter's. This matters when the assembly being rewritten does not sit
            // beside its references, as when a build rewrites a staged copy of its compiler output.
            if (this.Options.DependencySearchPaths != null)
            {
                foreach (var dependencySearchPath in this.Options.DependencySearchPaths)
                {
                    assemblyResolver.AddSearchDirectory(dependencySearchPath);
                }
            }

            assemblyResolver.AddSearchDirectory(
                Path.GetDirectoryName(typeof(Types.Threading.Tasks.Task).Assembly.Location));

            // Add shared framework directories discovered from the target assembly's runtime config.
            this.FrameworkInventoryRoots = AddSharedFrameworkDirectories(assemblyResolver, path,
                fileSystem, getEnvironmentVariable);

            // Snapshotted here, where every directory has been added and none has been searched yet, so
            // that it cannot fall behind the resolver and does not have to be read back out of it once
            // it is disposed, which is after the cache records this assembly.
            this.SearchDirectories = assemblyResolver.GetSearchDirectories();

            // Add the assembly resolution error handler.
            assemblyResolver.ResolveFailure += handler;

            this.Resolver = assemblyResolver;
            var readerParameters = new ReaderParameters()
            {
                AssemblyResolver = assemblyResolver,
                ReadSymbols = this.IsSymbolFileAvailable()
            };

            this.Definition = AssemblyDefinition.ReadAssembly(this.FilePath, readerParameters);
            this.FullName = this.Definition.FullName;
            this.ReferenceNames = this.Definition.Modules
                .SelectMany(module => module.AssemblyReferences)
                .Select(reference => reference.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(referenceName => referenceName, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Loads and returns the topological sorted list of unique assemblies to rewrite.
        /// </summary>
        internal static IEnumerable<AssemblyInfo> LoadAssembliesToRewrite(RewritingOptions options,
            AssemblyResolveEventHandler handler, IFileSystem fileSystem,
            Func<string, string> getEnvironmentVariable)
        {
            // Add all explicitly requested assemblies.
            var assemblies = new HashSet<AssemblyInfo>();
            try
            {
                foreach (string path in options.AssemblyPaths)
                {
                    if (!assemblies.Any(assembly => assembly.FilePath == path))
                    {
                        var name = Path.GetFileName(path);
                        if (options.IsAssemblyIgnored(name))
                        {
                            throw new InvalidOperationException($"Rewriting assembly '{name}' ({path}) that is in the ignore list.");
                        }

                        assemblies.Add(new AssemblyInfo(name, path, options, handler, fileSystem,
                            getEnvironmentVariable));
                    }
                }

                // Find direct dependencies to each assembly and load them, if the corresponding option is enabled.
                foreach (var assembly in assemblies)
                {
                    assembly.LoadDependencies(assemblies, handler);
                }

                // Validate that all assemblies are eligible for rewriting.
                foreach (var assembly in assemblies)
                {
                    assembly.ValidateAssembly();
                }

                return SortAssemblies(assemblies);
            }
            catch
            {
                // Each of these holds its file open through Cecil until it is disposed, and the caller
                // has no handle on them to dispose once loading did not complete. Leaving them behind
                // would lock the very assemblies that recovering from a validation failure asks the
                // user to rebuild.
                foreach (var assembly in assemblies)
                {
                    assembly.Dispose();
                }

                throw;
            }
        }

        /// <summary>
        /// Invokes the specified analysis or transformation pass on the assembly.
        /// </summary>
        internal void Invoke(Pass pass)
        {
            // Note that members are passed as format arguments, so that the full name of a member,
            // which Cecil rebuilds into a new string on each access, is only computed if the debug
            // message actually gets logged.
            pass.VisitAssembly(this);
            foreach (var module in this.Definition.Modules)
            {
                pass.LogWriter.LogDebug("....... Module: {0} ({1})", module.Name, module.FileName);
                pass.VisitModule(module);
                foreach (var type in module.GetTypes())
                {
                    if (!pass.VisitsSkippedTypes && type.CustomAttributes.Any(
                        attr => Pass.IsTypeOf(attr.AttributeType, typeof(SkipRewritingAttribute))))
                    {
                        // Skip rewriting this type. Passes that only report on the assembly opt out of
                        // this, because what they report is not confined to the IL that gets rewritten.
                        pass.LogWriter.LogDebug("......... Type: {0} [SKIP]", type);
                        continue;
                    }

                    pass.LogWriter.LogDebug("......... Type: {0}", type);
                    pass.VisitType(type);
                    foreach (var field in type.Fields.ToArray())
                    {
                        pass.LogWriter.LogDebug("........... Field: {0}", field);
                        pass.VisitField(field);
                    }

                    if (!pass.VisitsMethodBodies)
                    {
                        // Reading a body below is what makes Cecil materialize it from the image, so a
                        // pass that derives nothing from bodies skips the walk entirely rather than
                        // paying for every one of them and discarding the result.
                        continue;
                    }

                    foreach (var method in type.Methods.ToArray())
                    {
                        if (method.Body is null)
                        {
                            continue;
                        }

                        pass.LogWriter.LogDebug("........... Method {0}", method);
                        pass.VisitMethod(method);
                        if (pass is RewritingPass rewritingPass && rewritingPass.IsMethodBodyModified)
                        {
                            RewritingPass.FixInstructionOffsets(method);
                            rewritingPass.IsMethodBodyModified = false;
                        }
                    }
                }
            }

            pass.CompleteVisit();
        }

        /// <summary>
        /// Writes the assembly to the specified output path.
        /// </summary>
        internal void Write(string outputPath)
        {
            var writerParameters = new WriterParameters()
            {
                WriteSymbols = this.IsSymbolFileAvailable(),
                SymbolWriterProvider = new PortablePdbWriterProvider()
            };

            this.Definition.Write(outputPath, writerParameters);
        }

        /// <summary>
        /// Applies the <see cref="RewritingSignatureAttribute"/> attribute to the assembly. This attribute
        /// indicates that the assembly has been rewritten with the current version of Coyote and contains
        /// a signature identifying the parameters used during binary rewriting of the assembly.
        /// </summary>
        internal void ApplyRewritingSignatureAttribute(Version rewriterVersion)
        {
            var signature = new AssemblySignature(this, this.Dependencies, rewriterVersion, this.Options);
            var signatureHash = signature.ComputeHash();

            CustomAttribute attribute = this.GetCustomAttribute(typeof(RewritingSignatureAttribute));
            var versionAttributeArgument = new CustomAttributeArgument(
                this.Definition.MainModule.ImportReference(typeof(string)), rewriterVersion.ToString());
            var idAttributeArgument = new CustomAttributeArgument(
                this.Definition.MainModule.ImportReference(typeof(string)), signatureHash);
            if (attribute is null)
            {
                MethodReference attributeConstructor = this.Definition.MainModule.ImportReference(
                    typeof(RewritingSignatureAttribute).GetConstructor(new Type[] { typeof(string), typeof(string) }));
                attribute = new CustomAttribute(attributeConstructor);
                attribute.ConstructorArguments.Add(versionAttributeArgument);
                attribute.ConstructorArguments.Add(idAttributeArgument);
                this.Definition.CustomAttributes.Add(attribute);
            }
            else
            {
                attribute.ConstructorArguments[0] = versionAttributeArgument;
                attribute.ConstructorArguments[1] = idAttributeArgument;
            }

            this.IsRewritten = true;
        }

        /// <summary>
        /// Checks if this assembly has been rewritten and, if yes, returns its version and signature.
        /// </summary>
        /// <returns>True if the assembly has been rewritten with the same signature, else false.</returns>
        private bool IsAssemblyRewritten(out string version, out string signatureHash)
        {
            CustomAttribute attribute = this.GetCustomAttribute(typeof(RewritingSignatureAttribute));
            if (attribute != null)
            {
                version = attribute.ConstructorArguments[0].Value as string;
                signatureHash = attribute.ConstructorArguments[1].Value as string;
                return true;
            }

            version = string.Empty;
            signatureHash = string.Empty;
            return false;
        }

        /// <summary>
        /// Checks if the specified assembly is a mixed-mode assembly.
        /// </summary>
        /// <returns>True if the assembly only contains IL, else false.</returns>
        private bool IsMixedModeAssembly() =>
            this.Definition.Modules.Any(m => (m.Attributes & ModuleAttributes.ILOnly) is 0);

        /// <summary>
        /// Checks if the symbol file for the specified assembly is available.
        /// </summary>
        internal bool IsSymbolFileAvailable() =>
            this.FileSystem.FileExists(Path.ChangeExtension(this.FilePath, "pdb"));

        /// <summary>
        /// Returns the first found custom attribute with the specified type, if such an attribute
        /// is applied to the assembly, else null.
        /// </summary>
        private CustomAttribute GetCustomAttribute(Type attributeType) =>
            this.Definition.CustomAttributes.FirstOrDefault(
                attr => Pass.IsTypeOf(attr.AttributeType, attributeType));

        /// <summary>
        /// Validates that the assembly can be rewritten.
        /// </summary>
        private void ValidateAssembly()
        {
            if (this.IsAssemblyRewritten(out string version, out string signatureHash))
            {
                // The assembly has been already rewritten so check if the signatures match.
                var newVersion = Assembly.GetExecutingAssembly().GetName().Version;
                var newSignature = new AssemblySignature(this, this.Dependencies, newVersion, this.Options);
                var newSignatureHash = newSignature.ComputeHash();
                if (version != newVersion.ToString())
                {
                    throw new InvalidOperationException(
                        $"Assembly '{this.Name}' has been rewritten with a different coyote version.");
                }
                else if (signatureHash != newSignatureHash)
                {
                    // Rewriting is not idempotent, so an assembly that was rewritten under different
                    // settings -- or by a different build of the rewriter -- cannot be brought up to
                    // date in place. The original has to be produced again, which is why this names
                    // the only way out rather than suggesting an option: nothing the rewriter is
                    // given can recover the original from what it is being handed.
                    throw new InvalidOperationException(
                        $"Assembly '{this.Name}' ({this.FilePath}) was rewritten with a different rewriting " +
                        "configuration, or by a different build of the rewriter. Rewriting is not idempotent, " +
                        "so it cannot be rewritten again in place. Rebuild the project that produces it, or " +
                        "delete it and build again, so that an unrewritten assembly is there to rewrite. " +
                        "Disabling incremental rewriting does not clear this, because it is the assembly " +
                        "itself, not the cache, that records the earlier run.");
                }

                this.IsRewritten = true;
            }
            else if (this.IsMixedModeAssembly())
            {
                // Mono.Cecil does not support writing mixed-mode assemblies.
                throw new InvalidOperationException($"Rewriting mixed-mode assembly '{this.Name}' is not supported.");
            }
        }

        /// <summary>
        /// Loads all dependent assemblies in the local assembly path.
        /// </summary>
        private void LoadDependencies(HashSet<AssemblyInfo> assemblies, AssemblyResolveEventHandler handler)
        {
            // Get the directory associated with this assembly.
            var assemblyDir = Path.GetDirectoryName(this.FilePath);

            // Perform a non-recursive depth-first search to find all dependencies.
            var stack = new Stack<AssemblyInfo>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var assembly = stack.Pop();
                foreach (var reference in assembly.Definition.Modules.SelectMany(module => module.AssemblyReferences))
                {
                    var fileName = reference.Name + ".dll";
                    var path = Path.Combine(assemblyDir, fileName);
                    if (this.FileSystem.FileExists(path) && !this.Options.IsAssemblyIgnored(fileName))
                    {
                        AssemblyInfo dependency = assemblies.FirstOrDefault(assembly => assembly.FilePath == path);
                        if (dependency is null && this.Options.IsRewritingDependencies)
                        {
                            var name = Path.GetFileName(path);
                            dependency = new AssemblyInfo(name, path, this.Options, handler, this.FileSystem,
                                this.GetEnvironmentVariable);
                            stack.Push(dependency);
                            assemblies.Add(dependency);
                        }

                        if (dependency != null)
                        {
                            this.Dependencies.Add(dependency);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Sorts the specified assemblies in topological ordering.
        /// </summary>
        private static IEnumerable<AssemblyInfo> SortAssemblies(HashSet<AssemblyInfo> assemblies)
        {
            var sortedAssemblies = new List<AssemblyInfo>();

            // Assemblies that have zero or visited dependencies.
            var nextAssemblies = new HashSet<AssemblyInfo>(
                assemblies.Where(assembly => assembly.Dependencies.Count is 0));

            // Sort the assemblies in topological ordering.
            while (nextAssemblies.Count > 0)
            {
                var nextAssembly = nextAssemblies.First();
                nextAssemblies.Remove(nextAssembly);
                sortedAssemblies.Add(nextAssembly);

                // Add all assemblies that have not been sorted yet and have all their dependencies visited
                // to the set of next assemblies to sort.
                foreach (var assembly in assemblies.Where(assembly => !sortedAssemblies.Contains(assembly)))
                {
                    if (assembly.Dependencies.IsSubsetOf(sortedAssemblies))
                    {
                        nextAssemblies.Add(assembly);
                    }
                }
            }

            if (sortedAssemblies.Count != assemblies.Count)
            {
                // There are cycles in the assembly dependencies. This should normally never
                // happen because C# does not allow cycles in assembly references.
                throw new InvalidOperationException("Detected circular assembly dependencies.");
            }

            return sortedAssemblies;
        }

        /// <summary>
        /// Returns the root directory of the .NET installation according to the specified file system
        /// and environment, or null if it cannot be determined.
        /// </summary>
        /// <remarks>
        /// Both are supplied rather than read directly because this answer used to be cached in a
        /// static for the lifetime of the process, which made it whatever the first caller resolved.
        /// A test that describes a different installation has to be able to get a different answer.
        /// </remarks>
        internal static string GetDotnetRoot(IFileSystem fileSystem, Func<string, string> getEnvironmentVariable)
        {
            // Check the DOTNET_ROOT environment variable first.
            string dotnetRoot = getEnvironmentVariable(
                Environment.Is64BitProcess ? "DOTNET_ROOT" : "DOTNET_ROOT(x86)");
            if (!string.IsNullOrEmpty(dotnetRoot) && fileSystem.DirectoryExists(dotnetRoot))
            {
                return dotnetRoot;
            }

            // Derive from the current runtime directory.
            // RuntimeEnvironment.GetRuntimeDirectory() returns e.g.:
            //   C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.0\
            // Navigate up 3 levels: version -> framework name -> shared -> dotnet root.
            string runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            string candidate = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
            if (fileSystem.DirectoryExists(Path.Combine(candidate, "shared")))
            {
                return candidate;
            }

            return null;
        }

        /// <summary>
        /// Reads the target assembly's .runtimeconfig.json to extract the list of
        /// shared framework dependencies (name + minimum version).
        /// </summary>
        private static List<(string Name, string Version)> GetFrameworksFromRuntimeConfig(string assemblyPath,
            IFileSystem fileSystem)
        {
            var frameworks = new List<(string, string)>();

            // Look for the .runtimeconfig.json next to the assembly.
            string configPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
            if (!fileSystem.FileExists(configPath))
            {
                return frameworks;
            }

            try
            {
                string json = fileSystem.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var runtimeOptions = doc.RootElement.GetProperty("runtimeOptions");

                if (runtimeOptions.TryGetProperty("frameworks", out var frameworksArray))
                {
                    foreach (var fw in frameworksArray.EnumerateArray())
                    {
                        string name = fw.GetProperty("name").GetString();
                        string version = fw.GetProperty("version").GetString();
                        frameworks.Add((name, version));
                    }
                }
                else if (runtimeOptions.TryGetProperty("framework", out var singleFramework))
                {
                    // Older format uses singular "framework" property.
                    string name = singleFramework.GetProperty("name").GetString();
                    string version = singleFramework.GetProperty("version").GetString();
                    frameworks.Add((name, version));
                }
            }
            catch
            {
                // Silently ignore malformed runtime config files.
            }

            return frameworks;
        }

        /// <summary>
        /// Finds the best matching installed shared framework directory for the given
        /// framework name and minimum version, using major-version roll-forward semantics.
        /// </summary>
        private static string ResolveFrameworkDirectory(string dotnetRoot, string frameworkName,
            string minimumVersion, IFileSystem fileSystem)
        {
            string frameworkBase = Path.Combine(dotnetRoot, "shared", frameworkName);
            if (!fileSystem.DirectoryExists(frameworkBase))
            {
                return null;
            }

            if (!Version.TryParse(minimumVersion, out var requestedVersion))
            {
                return null;
            }

            string bestMatch = null;
            Version bestVersion = null;

            foreach (string dir in fileSystem.GetDirectories(frameworkBase, "*", false))
            {
                string versionStr = Path.GetFileName(dir);
                // Strip pre-release suffixes (e.g. "8.0.0-preview.1").
                int dashIndex = versionStr.IndexOf('-');
                string cleanVersion = dashIndex >= 0 ? versionStr.Substring(0, dashIndex) : versionStr;

                if (Version.TryParse(cleanVersion, out var candidateVersion) &&
                    candidateVersion.Major == requestedVersion.Major &&
                    candidateVersion >= requestedVersion)
                {
                    if (bestVersion is null || candidateVersion > bestVersion)
                    {
                        bestVersion = candidateVersion;
                        bestMatch = dir;
                    }
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Discovers shared framework directories from the target assembly's runtime
        /// configuration and adds them as search directories to the assembly resolver.
        /// </summary>
        /// <returns>
        /// The parent directory of each framework that was asked for, whether or not a version of it
        /// was found. Returned rather than only the winners because these describe the candidates
        /// resolution chose between, which is what <see cref="FrameworkInventoryRoots"/> records.
        /// </returns>
        private static IReadOnlyList<string> AddSharedFrameworkDirectories(DefaultAssemblyResolver resolver,
            string assemblyPath, IFileSystem fileSystem, Func<string, string> getEnvironmentVariable)
        {
            var inventoryRoots = new List<string>();
            string dotnetRoot = GetDotnetRoot(fileSystem, getEnvironmentVariable);
            if (dotnetRoot is null)
            {
                return inventoryRoots;
            }

            var frameworks = GetFrameworksFromRuntimeConfig(assemblyPath, fileSystem);
            foreach (var (name, version) in frameworks)
            {
                // Recorded before the resolution below and whether or not it succeeds: a framework
                // that is asked for and not installed today is one whose arrival tomorrow changes
                // what resolves, so the empty answer is worth as much as a found one.
                inventoryRoots.Add(Path.Combine(dotnetRoot, "shared", name));

                string frameworkDir = ResolveFrameworkDirectory(dotnetRoot, name, version, fileSystem);
                if (frameworkDir != null)
                {
                    resolver.AddSearchDirectory(frameworkDir);
                }
            }

            return inventoryRoots;
        }

        /// <summary>
        /// Determines whether the specified object is equal to the current object.
        /// </summary>
        public override bool Equals(object obj) => obj is AssemblyInfo info && this.FullName == info.FullName;

        /// <summary>
        /// Returns the hash code for this instance.
        /// </summary>
        public override int GetHashCode() => this.FullName.GetHashCode();

        /// <summary>
        /// Returns a string that represents the current assembly.
        /// </summary>
        public override string ToString() => this.FullName;

        /// <summary>
        /// Disposes the resources held by this object.
        /// </summary>
        public void Dispose()
        {
            if (!this.IsDisposed)
            {
                this.Definition?.Dispose();
                this.Resolver?.Dispose();
                this.IsDisposed = true;
            }
        }
    }
}
