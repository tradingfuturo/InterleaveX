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
    /// Requires each test assembly that constructs a testing engine to carry the per-assembly seed
    /// isolation guard that freezes those construction sites.
    /// </summary>
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
            "Project '{0}' constructs TestingEngine but has no deterministic seed isolation guard",
            "Determinism",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

        private static readonly DiagnosticDescriptor StaleGuard = new DiagnosticDescriptor(
            StaleGuardDiagnosticId,
            "Deterministic seed guard is stale",
            "Project '{0}' declares a deterministic seed isolation guard but constructs no TestingEngine",
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
