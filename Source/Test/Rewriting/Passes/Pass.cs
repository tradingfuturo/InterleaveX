// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Coyote.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SystemCompiler = System.Runtime.CompilerServices;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// An abstract implementation of a pass that traverses IL using a visitor pattern.
    /// </summary>
    internal abstract class Pass
    {
        /// <summary>
        /// The current assembly being visited.
        /// </summary>
        protected AssemblyInfo Assembly { get; private set; }

        /// <summary>
        /// The current module being visited.
        /// </summary>
        protected ModuleDefinition Module { get; private set; }

        /// <summary>
        /// The type being visited.
        /// </summary>
        protected TypeDefinition TypeDef { get; private set; }

        /// <summary>
        /// The current method being visited.
        /// </summary>
        protected MethodDefinition Method { get; private set; }

        /// <summary>
        /// A helper for transforming method bodies.
        /// </summary>
        protected ILProcessor Processor { get; private set; }

        /// <summary>
        /// Cache of qualified names.
        /// </summary>
        private static readonly Dictionary<string, string> CachedQualifiedNames = new Dictionary<string, string>();

        /// <summary>
        /// The file paths of the assemblies that are being visited.
        /// </summary>
        private readonly HashSet<string> VisitedAssemblyPaths;

        /// <summary>
        /// Cache of methods imported in the module that is currently being visited.
        /// </summary>
        private readonly Dictionary<Type, Dictionary<string, MethodReference>> ImportedMethods;

        /// <summary>
        /// Responsible for writing to the installed <see cref="ILogger"/>.
        /// </summary>
        protected internal readonly LogWriter LogWriter;

        /// <summary>
        /// True if the currently visited type is compiler generated.
        /// </summary>
        protected bool IsCompilerGeneratedType { get; private set; }

        /// <summary>
        /// True if the currently visited type is a generated async state machine.
        /// </summary>
        protected bool IsAsyncStateMachineType { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Pass"/> class.
        /// </summary>
        protected Pass(IEnumerable<AssemblyInfo> visitedAssemblies, LogWriter logWriter)
        {
            this.VisitedAssemblyPaths = new HashSet<string>(
                visitedAssemblies.Select(assembly => assembly.FilePath), StringComparer.Ordinal);
            this.ImportedMethods = new Dictionary<Type, Dictionary<string, MethodReference>>();
            this.LogWriter = logWriter;
        }

        /// <summary>
        /// True if this pass visits types that are declared with <see cref="SkipRewritingAttribute"/>.
        /// </summary>
        /// <remarks>
        /// That attribute exempts a type from being instrumented, which is a statement about the IL of
        /// the type rather than about the behavior of the program it belongs to. A pass that only reports
        /// on an assembly must therefore still see such types, because the behavior it reports on is just
        /// as present in them. Set by <see cref="AnalysisPass"/>, which is the only kind of pass that may.
        /// </remarks>
        protected internal virtual bool VisitsSkippedTypes => false;

        /// <summary>
        /// True if this pass visits the body of each method it encounters.
        /// </summary>
        /// <remarks>
        /// Reading a body is what makes Cecil materialize its instructions, variables and exception
        /// handlers from the image, so a pass that derives nothing from bodies should not ask for them:
        /// doing so is the most expensive part of a traversal, and it is paid for every assembly on
        /// every build. Cleared by <see cref="AnalysisPass"/>.
        /// </remarks>
        protected internal virtual bool VisitsMethodBodies => true;

        /// <summary>
        /// Visits the specified <see cref="AssemblyInfo"/>.
        /// </summary>
        /// <param name="assembly">The assembly to visit.</param>
        protected internal virtual void VisitAssembly(AssemblyInfo assembly)
        {
            this.Assembly = assembly;
            this.Module = null;
            this.TypeDef = null;
            this.Method = null;
            this.Processor = null;
        }

        /// <summary>
        /// Visits the specified <see cref="ModuleDefinition"/> inside the currently
        /// visited <see cref="AssemblyDefinition"/>.
        /// </summary>
        /// <param name="module">The module definition to visit.</param>
        protected internal virtual void VisitModule(ModuleDefinition module)
        {
            this.Module = module;
            this.TypeDef = null;
            this.Method = null;
            this.Processor = null;

            // Imported references are scoped to a module, so invalidate the cache.
            this.ImportedMethods.Clear();
        }

        /// <summary>
        /// Visits the specified <see cref="TypeDefinition"/> inside the currently
        /// visited <see cref="ModuleDefinition"/>.
        /// </summary>
        /// <param name="type">The type definition to visit.</param>
        protected internal virtual void VisitType(TypeDefinition type)
        {
            this.TypeDef = type;
            this.Method = null;
            this.Processor = null;
            this.IsCompilerGeneratedType = type.CustomAttributes.Any(
                attr => IsTypeOf(attr.AttributeType, typeof(SystemCompiler.CompilerGeneratedAttribute)));
            this.IsAsyncStateMachineType = type.Interfaces.Any(
                i => IsTypeOf(i.InterfaceType, typeof(SystemCompiler.IAsyncStateMachine)));
        }

        /// <summary>
        /// Visits the specified <see cref="FieldDefinition"/> inside the currently
        /// visited <see cref="TypeDefinition"/>.
        /// </summary>
        /// <param name="field">The field definition to visit.</param>
        protected internal virtual void VisitField(FieldDefinition field)
        {
        }

        /// <summary>
        /// Visits the specified <see cref="MethodDefinition"/> inside the currently
        /// visited <see cref="TypeDefinition"/>.
        /// </summary>
        /// <param name="method">The method definition to visit.</param>
        protected internal virtual void VisitMethod(MethodDefinition method)
        {
            this.Method = method;

            // Only non-abstract method bodies can be visited.
            if (!method.IsAbstract)
            {
                this.Processor = method.Body.GetILProcessor();

                // Visit the method body.
                this.VisitMethodBody(method.Body);

                // Visit the method body variables.
                foreach (var variable in method.Body.Variables.ToArray())
                {
                    this.VisitVariable(variable);
                }

                // Visit the method body instructions.
                Instruction instruction = method.Body.Instructions.FirstOrDefault();
                while (instruction != null)
                {
                    instruction = this.VisitInstruction(instruction);
                    instruction = instruction.Next;
                }
            }
        }

        /// <summary>
        /// Visits the specified <see cref="MethodBody"/> inside the currently
        /// visited <see cref="MethodDefinition"/>.
        /// </summary>
        /// <param name="body">The method body to visit.</param>
        protected virtual void VisitMethodBody(MethodBody body)
        {
        }

        /// <summary>
        /// Visits the specified <see cref="VariableDefinition"/> inside the currently
        /// visited <see cref="MethodDefinition"/>.
        /// </summary>
        /// <param name="variable">The variable definition to visit.</param>
        protected virtual void VisitVariable(VariableDefinition variable)
        {
        }

        /// <summary>
        /// Visits the specified IL <see cref="Instruction"/> inside the body of the currently
        /// visited <see cref="MethodDefinition"/>.
        /// </summary>
        /// <param name="instruction">The instruction to visit.</param>
        /// <returns>The last modified instruction, or the original if it was not changed.</returns>
        protected virtual Instruction VisitInstruction(Instruction instruction)
        {
            return instruction;
        }

        /// <summary>
        /// Completes the visit over the current assembly.
        /// </summary>
        protected internal virtual void CompleteVisit()
        {
        }

        /// <summary>
        /// Returns the specified method of the given provider type, imported in the module that is
        /// currently being visited, or null if it cannot be resolved.
        /// </summary>
        /// <remarks>
        /// Resolving and importing a method is expensive, so the result is cached until the next
        /// module is visited, including a failed lookup, so that it is not retried for every
        /// instruction. Invoke this from a visitor, so that any <see cref="AssemblyResolutionException"/>
        /// is handled by the same handler as the rest of the visit.
        /// </remarks>
        protected MethodReference TryImportMethod(Type providerType, string methodName)
        {
            if (!this.ImportedMethods.TryGetValue(providerType, out Dictionary<string, MethodReference> methods))
            {
                methods = new Dictionary<string, MethodReference>();
                this.ImportedMethods.Add(providerType, methods);
            }

            if (!methods.TryGetValue(methodName, out MethodReference method))
            {
                TypeDefinition resolvedType = this.Module.ImportReference(providerType).Resolve();
                MethodDefinition resolvedMethod = resolvedType?.Methods.FirstOrDefault(m => m.Name == methodName);
                method = resolvedMethod is null ? null : this.Module.ImportReference(resolvedMethod);
                methods.Add(methodName, method);
            }

            return method;
        }

        /// <summary>
        /// Checks if the specified type reference denotes the specified runtime type.
        /// </summary>
        /// <remarks>
        /// The namespace and name are compared separately, as this is much cheaper than comparing
        /// the full name, which Cecil rebuilds on each access.
        /// </remarks>
        internal static bool IsTypeOf(TypeReference reference, Type type) =>
            reference.Name == type.Name && reference.Namespace == type.Namespace;

        /// <summary>
        /// Returns true if the specified <see cref="MethodReference"/> can be resolved,
        /// as well as return the resolved method definition, else return false.
        /// </summary>
        protected bool TryResolve(MethodReference method, out MethodDefinition resolved, bool logError = true)
        {
            try
            {
                resolved = method?.Resolve();
            }
            catch
            {
                resolved = null;
            }

            if (logError && resolved is null && method != null)
            {
                // Pass the name as a format argument, so that the message (and the full name of the
                // method, which Cecil rebuilds on each access) is only formatted if it gets logged.
                this.LogWriter.LogWarning("Unable to resolve the '{0}' method. " +
                    "The method is either unsupported by Coyote, an external method not being rewritten, or the " +
                    ".NET platform of Coyote and the target assembly do not match.", method);
            }

            return resolved != null;
        }

        /// <summary>
        /// Returns true if the specified <see cref="TypeReference"/> can be resolved,
        /// as well as return the resolved type definition, else return false.
        /// </summary>
        protected bool TryResolve(TypeReference type, out TypeDefinition resolved, bool logError = true)
        {
            try
            {
                resolved = type?.Resolve();
            }
            catch
            {
                resolved = null;
            }

            if (logError && resolved is null && type != null)
            {
                // Pass the name as a format argument, so that the message (and the full name of the
                // type, which Cecil rebuilds on each access) is only formatted if it gets logged.
                this.LogWriter.LogWarning("Unable to resolve the '{0}' type. " +
                    "The type is either unsupported by Coyote, an external type not being rewritten, or the " +
                    ".NET platform of Coyote and the target assembly do not match.", type);
            }

            return resolved != null;
        }

        /// <summary>
        /// Gets the fully qualified name of the specified type.
        /// </summary>
        protected static string GetFullyQualifiedTypeName(TypeReference type)
        {
            if (!(type is GenericInstanceType genericType))
            {
                // The qualified name of a non-generic type is just its full name, so caching it
                // would cost a dictionary lookup keyed on that same full name.
                return type.FullName;
            }

            if (!CachedQualifiedNames.TryGetValue(type.FullName, out string name))
            {
                name = genericType.ElementType.FullName.Split('`')[0];
                CachedQualifiedNames.Add(type.FullName, name);
            }

            return name;
        }

        /// <summary>
        /// Gets the fully qualified name of the specified method.
        /// </summary>
        protected static string GetFullyQualifiedMethodName(MethodReference method)
        {
            if (!CachedQualifiedNames.TryGetValue(method.FullName, out string name))
            {
                string typeName = GetFullyQualifiedTypeName(method.DeclaringType);
                name = $"{typeName}.{method.Name}";
                CachedQualifiedNames.Add(method.FullName, name);
            }

            return name;
        }

        /// <summary>
        /// Checks if the specified type is a visited type.
        /// </summary>
        /// <remarks>
        /// Any type from an assembly being visited is a visited type.
        /// </remarks>
        protected bool IsVisitedType(TypeDefinition type)
        {
            if (type != null)
            {
                if (type.Module == this.Module ||
                    (type.Module.FileName != null && this.VisitedAssemblyPaths.Contains(type.Module.FileName)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if the specified type is a runtime type.
        /// </summary>
        /// <remarks>
        /// Any type from the Coyote assemblies is a runtime type.
        /// </remarks>
        protected static bool IsRuntimeType(TypeReference type) => IsRuntimeType(type?.Resolve());

        /// <summary>
        /// Checks if the specified type is a runtime type.
        /// </summary>
        /// <remarks>
        /// Any type from the Coyote assemblies is a runtime type.
        /// </remarks>
        protected static bool IsRuntimeType(TypeDefinition type)
        {
            if (type != null)
            {
                // Any type from the Coyote assemblies is not a foreign type.
                string module = type.Module.Name;
                if (module is "Microsoft.Coyote.dll" ||
                    module is "Microsoft.Coyote.Actors.dll" ||
                    module is "Microsoft.Coyote.Test.dll")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if the specified type is a foreign type.
        /// </summary>
        /// <remarks>
        /// Any type not visited that is not a runtime type is a foreign type.
        /// </remarks>
        protected bool IsForeignType(TypeReference type)
        {
            TypeDefinition resolvedType = type?.Resolve();
            return !this.IsVisitedType(resolvedType) && !IsRuntimeType(resolvedType);
        }

        /// <summary>
        /// Checks if the specified type is a system type.
        /// </summary>
        /// <remarks>
        /// Any type in the system namespace is a system type.
        /// </remarks>
        protected static bool IsSystemType(TypeReference type)
        {
            if (type != null)
            {
                TypeReference declaringType = type;
                while (declaringType.IsNested)
                {
                    declaringType = declaringType.DeclaringType;
                }

                if (declaringType.Namespace is "System" || declaringType.Namespace.StartsWith("System."))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
