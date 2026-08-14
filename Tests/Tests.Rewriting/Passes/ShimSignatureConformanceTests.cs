// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Coyote.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Rewriting.Tests
{
    /// <summary>
    /// Checks every replacement method against the method it stands in for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rewriting redirects a call site to a replacement it finds by signature, so a replacement whose
    /// signature has drifted from the method it replaces is not a compile error and not a test failure:
    /// it simply stops being found, the call keeps its original target, and whatever the replacement
    /// existed to model silently stops being modelled. A misspelled name and a renamed parameter both
    /// fail that way, and a wrong return type used to fail worse, by redirecting the call to a method
    /// that leaves the evaluation stack short.
    /// </para>
    /// <para>
    /// This test is the gate that makes that class of drift loud. It walks the same map the rewriter
    /// walks, applies the same matching rules, and reports every replacement the rewriter would fail to
    /// find — so the answer to "is this replacement still wired up?" comes from the build rather than
    /// from noticing that a race stopped being reported.
    /// </para>
    /// </remarks>
    public class ShimSignatureConformanceTests : BaseRewritingTest
    {
        public ShimSignatureConformanceTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// Replacement methods that deliberately do not mirror a method of the type they model, keyed
        /// by declaring type and method name. Each entry needs a reason: this list is the seam through
        /// which the drift this test exists to catch could be waved through.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> KnownDivergences =
            new Dictionary<string, string>
            {
                ["Microsoft.Coyote.Rewriting.Types.Net.Http.HttpClient::Control"] =
                    "Injected by name from InterAssemblyInvocationRewritingPass rather than found by " +
                    "signature, and takes control of a client the caller already has instead of " +
                    "replacing anything the client declares.",
                ["Microsoft.Coyote.Runtime.CompilerServices.TaskAwaiter::Wrap"] =
                    "Injected by name from InterAssemblyInvocationRewritingPass to adopt an awaiter " +
                    "that came back from an assembly that was not rewritten; the awaiter it adopts " +
                    "has no such method to replace.",
                ["Microsoft.Coyote.Runtime.CompilerServices.ValueTaskAwaiter::Wrap"] =
                    "Injected by name from InterAssemblyInvocationRewritingPass, as TaskAwaiter.Wrap is.",
            };

        /// <summary>
        /// Signature-specific compiler entry points that are selected by specialized rewriting passes
        /// instead of the ordinary replacement lookup.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> KnownSignatureDivergences =
            new Dictionary<string, string>
            {
                ["Microsoft.Coyote.Rewriting.Types.Hosting.BackgroundService::DisposeBase(BackgroundService)"] =
                    "Selected explicitly for a nonvirtual base call.",
                ["Microsoft.Coyote.Rewriting.Types.Hosting.BackgroundService::get_ExecuteTaskBase(BackgroundService)"] =
                    "Selected explicitly for a nonvirtual base call.",
                ["Microsoft.Coyote.Rewriting.Types.Hosting.BackgroundService::IsExecutionControlled()"] =
                    "Injected as the policy branch for rewritten base calls.",
                ["Microsoft.Coyote.Rewriting.Types.Hosting.BackgroundService::StartAsyncBase(BackgroundService, CancellationToken)"] =
                    "Selected explicitly for a nonvirtual base call.",
                ["Microsoft.Coyote.Rewriting.Types.Hosting.BackgroundService::StopAsyncBase(BackgroundService, CancellationToken)"] =
                    "Selected explicitly for a nonvirtual base call.",
                ["Microsoft.Coyote.Rewriting.Types.Hosting.Host::Dispose(IHost)"] =
                    "Selected by the IDisposable router for IHost instances.",
                ["Microsoft.Coyote.Rewriting.Types.Hosting.Host::DisposeAsync(IHost)"] =
                    "Selected by the IAsyncDisposable router for IHost instances.",
                ["Microsoft.Coyote.Rewriting.Types.Hosting.Host::get_Services(T&)"] =
                    "Closed and selected explicitly for constrained generic calls.",
                ["Microsoft.Coyote.Rewriting.Types.Hosting.Host::IsFrameworkHost(IHost)"] =
                    "Internal runtime identity predicate, not a replacement.",
                ["Microsoft.Coyote.Rewriting.Types.Hosting.Host::StartAsync(T&, CancellationToken)"] =
                    "Closed and selected explicitly for constrained generic calls.",
                ["Microsoft.Coyote.Rewriting.Types.Hosting.Host::StopAsync(T&, CancellationToken)"] =
                    "Closed and selected explicitly for constrained generic calls.",
                ["Microsoft.Coyote.Rewriting.Types.Hosting.HostedService::StartAsync(T&, CancellationToken)"] =
                    "Closed and selected explicitly for constrained generic calls.",
                ["Microsoft.Coyote.Rewriting.Types.Hosting.HostedService::StopAsync(T&, CancellationToken)"] =
                    "Closed and selected explicitly for constrained generic calls.",
            };

        /// <summary>
        /// Methods of a guarded collection that knowingly have no replacement, keyed by declaring type
        /// and signature. Each entry is an access that runs unguarded, so each needs a reason.
        /// </summary>
        /// <remarks>
        /// An entry naming a member that the running target framework does not declare is simply never
        /// consulted, and is not reported as stale: alternate lookup and the capacity-taking overload of
        /// TrimExcess arrived in .NET 9, and this runs on .NET 8 as well.
        /// </remarks>
        private static readonly IReadOnlyDictionary<string, string> UnguardedMembers =
            new Dictionary<string, string>
            {
                // Reads. A concurrent write overlapping one of these is not reported, which is a
                // narrower loss than an unguarded write, and each is a single field or a copy.
                ["Dictionary`2::get_Capacity()"] = "Read of a single field.",
                ["Dictionary`2::get_Comparer()"] = "Read of a single field.",
                ["HashSet`1::get_Capacity()"] = "Read of a single field.",
                ["List`1::AsReadOnly()"] = "Read that hands back a view; the view itself is not modelled.",
                ["List`1::Slice(Int32, Int32)"] = "Read that copies out; the copy itself is not modelled.",

                // Alternate lookup, .NET 9 and later. Not one missing replacement each but a door: the
                // returned struct is a view over the same storage, and every operation performed through
                // it reaches the collection without passing a replacement. Closing this means modelling
                // the view type, which is a feature rather than a fix, and is tracked as one.
                ["Dictionary`2::GetAlternateLookup()"] = "Returns an unmodelled view over the storage.",
                ["Dictionary`2::TryGetAlternateLookup(AlternateLookup`1&)"] =
                    "Returns an unmodelled view over the storage.",
                ["HashSet`1::GetAlternateLookup()"] = "Returns an unmodelled view over the storage.",
                ["HashSet`1::TryGetAlternateLookup(AlternateLookup`1&)"] =
                    "Returns an unmodelled view over the storage.",
            };

        /// <summary>
        /// Types touched so that the assemblies declaring the modelled types are loaded before the
        /// modelled types are looked up by name.
        /// </summary>
        private static readonly Type[] ProbedAssemblyAnchors =
        {
            typeof(object),
            typeof(System.Collections.Generic.List<int>),
            typeof(System.Collections.Concurrent.ConcurrentBag<int>),
            typeof(System.Threading.Tasks.Task),
            typeof(System.Threading.Tasks.Parallel),
            typeof(System.Net.Http.HttpClient),
            typeof(System.Threading.Channels.Channel),

            // The one modelled type that is not from the BCL, so no other anchor's assembly can resolve it.
            typeof(Microsoft.Extensions.Hosting.BackgroundService)
        };

        [Fact(Timeout = 60000)]
        public void TestEveryReplacementMethodMatchesTheMethodItReplaces()
        {
            var divergences = new List<string>();
            IReadOnlyDictionary<Type, List<Type>> replacements = GetReplacedTypes();
            Assert.NotEmpty(replacements);

            foreach (var replacement in replacements.OrderBy(entry => entry.Key.Name))
            {
                // The modelled types are closed over the replacement's own generic parameters, which
                // are the ones its members are written in terms of, so that everything below compares
                // like with like.
                List<Type> modelledTypes = replacement.Value
                    .Select(modelledType => Close(modelledType, replacement.Key)).ToList();
                CheckReplacementType(replacement.Key, modelledTypes, replacements, divergences);
            }

            Assert.True(divergences.Count is 0,
                $"Found {divergences.Count} replacement method(s) that the rewriter cannot match to the " +
                "method they replace:" + Environment.NewLine +
                string.Join(Environment.NewLine, divergences));
        }

        /// <summary>
        /// The other direction: a method of a guarded collection that has no replacement at all is not
        /// drift, so nothing above can see it, and every call to it runs unguarded.
        /// </summary>
        /// <remarks>
        /// Confined to the guarded collections, because those are the ones where a missing replacement
        /// costs correctness rather than fidelity: an unguarded mutator is a write the checker cannot
        /// see, and every race through it goes unreported. The rest of the replaced types are modelled
        /// selectively by design and would drown this in noise.
        /// </remarks>
        [Fact(Timeout = 60000)]
        public void TestEveryGuardedCollectionMethodHasAReplacement()
        {
            var unguarded = new List<string>();
            IReadOnlyDictionary<Type, List<Type>> replacements = GetReplacedTypes();

            foreach (var replacement in replacements.OrderBy(entry => entry.Key.Name))
            {
                if (!IsGuardedCollection(replacement.Key))
                {
                    continue;
                }

                foreach (Type modelledType in replacement.Value
                    .Select(modelledType => Close(modelledType, replacement.Key)))
                {
                    CheckModelledType(replacement.Key, modelledType, replacements, unguarded);
                }
            }

            Assert.True(unguarded.Count is 0,
                $"Found {unguarded.Count} method(s) of a guarded collection with no replacement, so " +
                "calls to them are not guarded:" + Environment.NewLine +
                string.Join(Environment.NewLine, unguarded));
        }

        [Fact(Timeout = 60000)]
        public void TestEveryModelledTypeIsResolvable()
        {
            var unresolved = new List<string>();
            foreach (string name in GetKnownTypes().Keys.OrderBy(name => name))
            {
                if (ResolveModelledType(name) is null)
                {
                    unresolved.Add(name);
                }
            }

            Assert.True(unresolved.Count is 0,
                "The rewriter is registered to replace types that cannot be found, so nothing it is " +
                "registered for can be checked:" + Environment.NewLine +
                string.Join(Environment.NewLine, unresolved));
        }

        [Fact(Timeout = 60000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestEveryReferenceInstanceStaticShimHasAConstrainedRoute()
        {
            MethodInfo capability = typeof(MethodBodyTypeRewritingPass).GetMethod(
                "CanRouteConstrainedStaticReceiverForTesting",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(capability);

            var unroutable = new List<string>();
            foreach (Type replacement in GetReplacedTypes().Keys.OrderBy(type => type.FullName))
            {
                foreach (MethodInfo method in replacement.GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    ParameterInfo receiver = method.GetParameters().FirstOrDefault();
                    if (receiver is null)
                    {
                        continue;
                    }

                    Type receiverType = receiver.ParameterType.IsByRef ?
                        receiver.ParameterType.GetElementType() : receiver.ParameterType;
                    if (receiverType.IsValueType || receiverType.IsPointer)
                    {
                        continue;
                    }

                    if (!(bool)capability.Invoke(null, new object[] { receiver.ParameterType }))
                    {
                        unroutable.Add(Describe(method));
                    }
                }
            }

            Assert.True(unroutable.Count is 0,
                "Reference receiver shims without a legal constrained route:" +
                Environment.NewLine + string.Join(Environment.NewLine, unroutable));
        }

        /// <summary>
        /// Checks every replacement method declared by the specified type.
        /// </summary>
        private static void CheckReplacementType(Type replacement, List<Type> modelledTypes,
            IReadOnlyDictionary<Type, List<Type>> replacements, List<string> divergences)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Static |
                BindingFlags.Instance | BindingFlags.DeclaredOnly;

            // On a static replacement type, 'Create' stands in for a constructor, which the rewriter
            // matches by name instead of by signature. On a replacement that is a type in its own
            // right, such as a method builder, 'Create' is a static method of the modelled type like
            // any other and is checked like any other.
            bool createStandsInForConstructor = replacement.IsSealed && replacement.IsAbstract;
            foreach (MethodInfo method in replacement.GetMethods(Flags).OrderBy(m => m.Name))
            {
                if (method.IsSpecialName && method.Name.StartsWith("op_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (createStandsInForConstructor && method.Name is "Create")
                {
                    continue;
                }

                if (method.GetBaseDefinition().DeclaringType == typeof(object))
                {
                    continue;
                }

                if (KnownDivergences.ContainsKey($"{replacement.FullName}::{method.Name}"))
                {
                    continue;
                }

                if (KnownSignatureDivergences.ContainsKey(
                    $"{replacement.FullName}::{DescribeSignature(method)}"))
                {
                    continue;
                }

                string divergence = Diagnose(method, modelledTypes, replacements);
                if (divergence != null)
                {
                    divergences.Add(divergence);
                }
            }
        }

        /// <summary>
        /// Checks that every method of the specified modelled type has a replacement.
        /// </summary>
        private static void CheckModelledType(Type replacement, Type modelledType,
            IReadOnlyDictionary<Type, List<Type>> replacements, List<string> unguarded)
        {
            MethodInfo[] candidates = replacement.GetMethods(BindingFlags.Public | BindingFlags.Static |
                BindingFlags.DeclaredOnly);

            foreach (MethodInfo method in modelledType.GetMethods(BindingFlags.Public |
                BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(m => m.Name))
            {
                if (method.GetBaseDefinition().DeclaringType == typeof(object))
                {
                    continue;
                }

                if (UnguardedMembers.ContainsKey($"{modelledType.Name}::{DescribeSignature(method)}"))
                {
                    continue;
                }

                if (!candidates.Any(candidate => candidate.Name == method.Name &&
                    CheckParametersMatch(candidate, method, modelledType, replacements)))
                {
                    unguarded.Add($"{modelledType.Name}::{DescribeSignature(method)} has no replacement.");
                }
            }
        }

        /// <summary>
        /// Checks if the specified replacement type is one whose accesses are guarded against races,
        /// as opposed to one that is modelled for some other reason.
        /// </summary>
        private static bool IsGuardedCollection(Type replacement) =>
            replacement.Namespace == "Microsoft.Coyote.Rewriting.Types.Collections.Generic";

        /// <summary>
        /// Returns a description of why the rewriter cannot match the specified replacement method to
        /// the method it replaces, or null if it can.
        /// </summary>
        private static string Diagnose(MethodInfo replacement, List<Type> modelledTypes,
            IReadOnlyDictionary<Type, List<Type>> replacements)
        {
            var named = new List<MethodInfo>();
            var parametersMatched = new List<MethodInfo>();
            foreach (Type modelledType in modelledTypes)
            {
                foreach (MethodInfo candidate in modelledType.GetMethods(BindingFlags.Public |
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (candidate.Name != replacement.Name)
                    {
                        continue;
                    }

                    named.Add(candidate);
                    if (!CheckParametersMatch(replacement, candidate, modelledType, replacements))
                    {
                        continue;
                    }

                    if (AreEquivalent(replacement.ReturnType, candidate.ReturnType, replacements))
                    {
                        return null;
                    }

                    parametersMatched.Add(candidate);
                }
            }

            if (parametersMatched.Count > 0)
            {
                return $"{Describe(replacement)} returns '{replacement.ReturnType}', but the " +
                    $"{Describe(parametersMatched[0])} it replaces returns " +
                    $"'{parametersMatched[0].ReturnType}'; the rewriter compares return types, so this " +
                    "replacement is never found and what it models is never modelled.";
            }

            if (named.Count > 0)
            {
                return $"{Describe(replacement)} matches no overload of '{replacement.Name}' on " +
                    $"'{modelledTypes[0]}'. Candidates: {string.Join("; ", named.Select(Describe))}.";
            }

            return $"{Describe(replacement)} replaces nothing: " +
                $"'{modelledTypes[0]}' declares no method named '{replacement.Name}'.";
        }

        /// <summary>
        /// Checks the parameters of a replacement method against the method it replaces, the same way
        /// the rewriter does, including the case where an instance method is replaced by a static one
        /// that takes the instance as its first parameter.
        /// </summary>
        private static bool CheckParametersMatch(MethodInfo replacement, MethodInfo candidate,
            Type modelledType, IReadOnlyDictionary<Type, List<Type>> replacements)
        {
            if (!replacement.IsStatic && candidate.IsStatic)
            {
                return false;
            }

            if (replacement.IsGenericMethodDefinition != candidate.IsGenericMethodDefinition ||
                replacement.GetGenericArguments().Length != candidate.GetGenericArguments().Length)
            {
                return false;
            }

            ParameterInfo[] replacementParameters = replacement.GetParameters();
            ParameterInfo[] candidateParameters = candidate.GetParameters();

            int offset = 0;
            if (replacement.IsStatic && !candidate.IsStatic)
            {
                // The instance the replaced method would have run on is passed explicitly, and by
                // reference when it is a value type.
                if (replacementParameters.Length != candidateParameters.Length + 1)
                {
                    return false;
                }

                Type instanceType = replacementParameters[0].ParameterType;
                if (instanceType.IsByRef)
                {
                    instanceType = instanceType.GetElementType();
                }

                if (!AreEquivalent(instanceType, modelledType, replacements))
                {
                    return false;
                }

                offset = 1;
            }
            else if (replacementParameters.Length != candidateParameters.Length)
            {
                return false;
            }

            for (int idx = 0; idx < candidateParameters.Length; ++idx)
            {
                ParameterInfo left = replacementParameters[idx + offset];
                ParameterInfo right = candidateParameters[idx];
                if (left.Name != right.Name ||
                    !AreEquivalent(left.ParameterType, right.ParameterType, replacements))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks if the specified types are the same type, or if the first is the replacement the
        /// rewriter installs for the second.
        /// </summary>
        private static bool AreEquivalent(Type left, Type right,
            IReadOnlyDictionary<Type, List<Type>> replacements)
        {
            if (left == right)
            {
                return true;
            }

            if (left.IsByRef || right.IsByRef)
            {
                return left.IsByRef && right.IsByRef &&
                    AreEquivalent(left.GetElementType(), right.GetElementType(), replacements);
            }

            if (left.IsArray || right.IsArray)
            {
                return left.IsArray && right.IsArray && left.GetArrayRank() == right.GetArrayRank() &&
                    AreEquivalent(left.GetElementType(), right.GetElementType(), replacements);
            }

            if (left.IsGenericParameter || right.IsGenericParameter)
            {
                // Compared by name, exactly as the rewriter does: it reads generic parameters straight
                // out of the metadata, where they carry no identity beyond their name.
                return left.IsGenericParameter && right.IsGenericParameter && left.Name == right.Name;
            }

            if (left.IsGenericType && right.IsGenericType)
            {
                // A generic type definition is treated as itself closed over its own generic
                // parameters, which is how a type refers to itself from inside its own body: the
                // return type of a builder's factory method is the builder, spelt as the definition.
                Type leftDefinition = left.GetGenericTypeDefinition();
                Type rightDefinition = right.GetGenericTypeDefinition();
                return (leftDefinition == rightDefinition ||
                    IsRegisteredReplacement(leftDefinition, rightDefinition, replacements)) &&
                    left.GetGenericArguments().Zip(right.GetGenericArguments(),
                        (l, r) => AreEquivalent(l, r, replacements)).All(match => match);
            }

            return IsRegisteredReplacement(left, right, replacements);
        }

        /// <summary>
        /// Checks if the rewriter is registered to replace the second type with the first.
        /// </summary>
        private static bool IsRegisteredReplacement(Type replacement, Type modelledType,
            IReadOnlyDictionary<Type, List<Type>> replacements) =>
            replacements.TryGetValue(replacement, out List<Type> modelledTypes) &&
            modelledTypes.Contains(modelledType);

        /// <summary>
        /// Closes the specified modelled type over the generic parameters of the replacement type, so
        /// that the two describe their members with the same generic parameters and can be compared by
        /// identity rather than by position.
        /// </summary>
        private static Type Close(Type modelledType, Type replacement)
        {
            if (!modelledType.IsGenericTypeDefinition || !replacement.IsGenericTypeDefinition)
            {
                return modelledType;
            }

            Type[] arguments = replacement.GetGenericArguments();
            if (arguments.Length != modelledType.GetGenericArguments().Length)
            {
                return modelledType;
            }

            try
            {
                return modelledType.MakeGenericType(arguments);
            }
            catch (ArgumentException)
            {
                // The replacement's generic parameters do not satisfy the modelled type's constraints,
                // which is itself a divergence, and is reported as an unmatched method below.
                return modelledType;
            }
        }

        /// <summary>
        /// Returns the map from each replacement type to the types it is registered to replace. A
        /// replacement can stand in for more than one type, as the one for a lock stands in both for
        /// the lock and for the scope its methods hand out.
        /// </summary>
        private static IReadOnlyDictionary<Type, List<Type>> GetReplacedTypes()
        {
            var result = new Dictionary<Type, List<Type>>();
            foreach (var entry in GetKnownTypes())
            {
                Type modelledType = ResolveModelledType(entry.Key);
                if (modelledType is null)
                {
                    // Reported on its own by TestEveryModelledTypeIsResolvable.
                    continue;
                }

                if (!result.TryGetValue(entry.Value, out List<Type> modelledTypes))
                {
                    modelledTypes = new List<Type>();
                    result.Add(entry.Value, modelledTypes);
                }

                modelledTypes.Add(modelledType);
            }

            return result;
        }

        /// <summary>
        /// Returns the map the rewriter uses from the full name of each replaced type to the type that
        /// replaces it, read from a pass rather than restated here, so that a replacement this test
        /// does not know about cannot slip past it.
        /// </summary>
        private static IReadOnlyDictionary<string, Type> GetKnownTypes()
        {
            RewritingOptions options = RewritingOptions.Create();
            options.IsDataRaceCheckingEnabled = true;
            options.IsRewritingConcurrentCollections = true;

            var pass = new MethodBodyTypeRewritingPass(options, Array.Empty<AssemblyInfo>(),
                new MemoryLogWriter(Coyote.Configuration.Create()));
            FieldInfo field = typeof(TypeRewritingPass).GetField("KnownTypes",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (Dictionary<string, Type>)field.GetValue(pass);
        }

        /// <summary>
        /// Resolves the type with the specified metadata full name, as the rewriter spells it.
        /// </summary>
        private static Type ResolveModelledType(string fullName)
        {
            // Metadata nests with a forward slash where reflection nests with a plus.
            string name = fullName.Replace('/', '+');
            Type type = Type.GetType(name);
            if (type != null)
            {
                return type;
            }

            foreach (Assembly assembly in ProbedAssemblyAnchors.Select(anchor => anchor.Assembly)
                .Concat(AppDomain.CurrentDomain.GetAssemblies()).Distinct())
            {
                type = assembly.GetType(name);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        /// <summary>
        /// Describes the specified method by name and parameter types, which is what identifies it
        /// within its own declaring type and so is what the allow-list is keyed on.
        /// </summary>
        private static string DescribeSignature(MethodInfo method) =>
            $"{method.Name}(" +
            string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)) + ")";

        /// <summary>
        /// Describes the specified method the way this test's failures read best.
        /// </summary>
        private static string Describe(MethodInfo method) =>
            $"'{method.DeclaringType.FullName ?? method.DeclaringType.ToString()}.{method.Name}(" +
            string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")) +
            ")'";
    }
}
