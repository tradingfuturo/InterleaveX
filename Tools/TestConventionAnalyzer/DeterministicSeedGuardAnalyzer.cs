// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace InterleaveX.TestConventionAnalyzer
{
    /// <summary>
    /// Requires each test assembly that builds a testing engine to carry the per-assembly seed
    /// isolation guard that freezes those build sites.
    /// </summary>
    /// <remarks>
    /// Both ways of building one count. Constructing it is the obvious one; asking the type for a
    /// new instance -- <c>TestingEngine.Create</c> and anything else static on it that hands one
    /// back -- reaches the same constructor through a method whose body lives in another assembly,
    /// so a scan for construction alone sees nothing at all. The factory is matched by what it
    /// returns rather than by its name, so a second one added beside <c>Create</c> is covered
    /// without anyone remembering this file exists.
    ///
    /// The member has to be declared on the engine itself. Matching any static method that returns
    /// one would also flag every caller of a test's own helper, and that helper already builds the
    /// engine somewhere this reports.
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DeterministicSeedGuardAnalyzer : DiagnosticAnalyzer
    {
        public const string MissingGuardDiagnosticId = "IXT001";
        public const string StaleGuardDiagnosticId = "IXT002";

        private const string TestingEngineType =
            "Microsoft.Coyote.SystematicTesting.TestingEngine";
        private const string GuardType =
            "Microsoft.Coyote.Tests.Common.Architecture.DeterministicSeedIsolationTestsBase";
        private const string SanctionedBuilderType =
            "Microsoft.Coyote.Tests.Common.BaseTest";

        private static readonly DiagnosticDescriptor MissingGuard = new DiagnosticDescriptor(
            MissingGuardDiagnosticId,
            "Testing engine construction is not guarded",
            "Project '{0}' builds a TestingEngine but has no deterministic seed isolation guard",
            "Determinism",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        private static readonly DiagnosticDescriptor StaleGuard = new DiagnosticDescriptor(
            StaleGuardDiagnosticId,
            "Deterministic seed guard is stale",
            "Project '{0}' declares a deterministic seed isolation guard but builds no TestingEngine",
            "Determinism",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(MissingGuard, StaleGuard);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(startContext =>
            {
                INamedTypeSymbol engineType = startContext.Compilation.GetTypeByMetadataName(TestingEngineType);
                INamedTypeSymbol guardType = startContext.Compilation.GetTypeByMetadataName(GuardType);
                if (engineType is null || guardType is null)
                {
                    return;
                }

                var builders = new ConcurrentBag<Location>();
                var guards = new ConcurrentBag<Location>();
                startContext.RegisterOperationAction(operationContext =>
                {
                    var creation = (IObjectCreationOperation)operationContext.Operation;
                    if (SymbolEqualityComparer.Default.Equals(creation.Type, engineType) &&
                        !IsSanctionedBuilder(operationContext.ContainingSymbol))
                    {
                        builders.Add(creation.Syntax.GetLocation());
                    }
                }, OperationKind.ObjectCreation);

                startContext.RegisterOperationAction(operationContext =>
                {
                    var invocation = (IInvocationOperation)operationContext.Operation;
                    if (IsEngineFactory(invocation.TargetMethod, engineType) &&
                        !IsSanctionedBuilder(operationContext.ContainingSymbol))
                    {
                        builders.Add(invocation.Syntax.GetLocation());
                    }
                }, OperationKind.Invocation);

                startContext.RegisterOperationAction(operationContext =>
                {
                    var methodReference = (IMethodReferenceOperation)operationContext.Operation;
                    if (IsEngineFactory(methodReference.Method, engineType) &&
                        !IsSanctionedBuilder(operationContext.ContainingSymbol))
                    {
                        builders.Add(methodReference.Syntax.GetLocation());
                    }
                }, OperationKind.MethodReference);

                startContext.RegisterSymbolAction(symbolContext =>
                {
                    var type = (INamedTypeSymbol)symbolContext.Symbol;
                    if (type.TypeKind is TypeKind.Class && !type.IsAbstract &&
                        DerivesFrom(type, guardType))
                    {
                        guards.Add(type.Locations.FirstOrDefault(location => location.IsInSource) ??
                            Location.None);
                    }
                }, SymbolKind.NamedType);

                startContext.RegisterCompilationEndAction(endContext =>
                {
                    if (!builders.IsEmpty && guards.IsEmpty)
                    {
                        Location location = builders.OrderBy(item => item.SourceSpan.Start).First();
                        endContext.ReportDiagnostic(Diagnostic.Create(
                            MissingGuard, location, endContext.Compilation.AssemblyName));
                    }
                    else if (builders.IsEmpty && !guards.IsEmpty)
                    {
                        Location location = guards.OrderBy(item => item.SourceSpan.Start).First();
                        endContext.ReportDiagnostic(Diagnostic.Create(
                            StaleGuard, location, endContext.Compilation.AssemblyName));
                    }
                });
            });
        }

        /// <summary>
        /// Returns true if the specified method is one the testing engine hands a new instance back
        /// from, rather than one that merely mentions the type.
        /// </summary>
        private static bool IsEngineFactory(IMethodSymbol method, INamedTypeSymbol engineType) =>
            method != null && method.IsStatic &&
            SymbolEqualityComparer.Default.Equals(method.ContainingType, engineType) &&
            SymbolEqualityComparer.Default.Equals(method.ReturnType, engineType);

        private static bool IsSanctionedBuilder(ISymbol symbol)
        {
            for (INamedTypeSymbol type = symbol?.ContainingType; type != null; type = type.ContainingType)
            {
                if (type.ToDisplayString() == SanctionedBuilderType)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
        {
            for (INamedTypeSymbol current = type.BaseType; current != null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
