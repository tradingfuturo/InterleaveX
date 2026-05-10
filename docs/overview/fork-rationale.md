## Fork rationale: why InterleaveX exists

**InterleaveX is a community-maintained fork of [Microsoft Coyote](https://github.com/microsoft/coyote).** This page explains who maintains it, why the fork was created, what changed and what didn't, and how to migrate from Microsoft.Coyote.

If you arrived expecting Microsoft Coyote documentation, you are mostly in the right place: the API surface, design, semantics, and the bulk of the documentation on this site are inherited from upstream and apply verbatim. Only the names and the maintenance owner have changed.

## Who maintains InterleaveX

InterleaveX is maintained by a small group of engineers who run production .NET software that depends on the systematic concurrency testing technique pioneered by Microsoft Coyote. We are not affiliated with Microsoft. We do not represent the original Coyote project, the research, or its design decisions.

## Why we forked

Microsoft Coyote remains the authoritative reference for the design and the research. We forked specifically to keep the toolchain alive on current and upcoming .NET LTS releases for our own production usage:

- Track .NET runtime updates as they ship and ensure the IL rewriter and runtime keep working on new BCL APIs.
- Continue evolving instrumentation against new BCL types our codebases adopt (concurrency primitives, web stack changes, etc.).
- Fix bugs we encounter in our own use of the tool.

We are not forking the design or attempting to compete with upstream. When in doubt, we prefer to mirror upstream choices and contribute fixes back where appropriate.

## What changed

This fork is a **branding-only rebrand** at the user-visible layer. The internal layer is intentionally preserved.

| Surface | Status |
|---------|--------|
| Product name | Microsoft Coyote → InterleaveX |
| NuGet package IDs | `Microsoft.Coyote.*` → `InterleaveX.*` (`InterleaveX`, `InterleaveX.Core`, `InterleaveX.Actors`, `InterleaveX.Test`, `InterleaveX.Tool`, `InterleaveX.CLI`) |
| CLI command | `coyote` → `interleavex` (a `coyote` alias is planned via a separate compatibility tool package; meanwhile users may shell-alias) |
| Strong-name signing key | New fork-owned `.snk` |
| Documentation | This site is rebranded to InterleaveX; upstream `aka.ms/learn-coyote` and similar URLs in CLI help are deliberately retained as upstream-doc references |
| Licensing | Dual-licensed: upstream Microsoft code remains MIT; new fork additions are GPL-3.0 (see [`NOTICE.md`](https://github.com/tradingfuturo/interleavex/blob/main/NOTICE.md) at the repo root) |

## What did NOT change

- **C# namespaces stay `Microsoft.Coyote.*`**: 32 namespaces across the codebase, kept exactly as upstream defines them.
- **Assembly DLL names stay `Microsoft.Coyote.dll`, `Microsoft.Coyote.Actors.dll`, `Microsoft.Coyote.Test.dll`**: these are required by the IL rewriter's hardcoded recognition of "runtime types," and renaming them would change rewriter behavior.
- **Class names stay**: `CoyoteRuntime`, `ICoyoteRuntime`, and other public types preserve their original names so consumer source code references continue to compile.
- **Test project assembly names stay `Microsoft.Coyote.Tests.*`**: required for the unchanged `InternalsVisibleTo` declarations.

This means **migrating from `Microsoft.Coyote.*` to InterleaveX does not require any source-code changes**. The using statements, the type references, the API calls — all stay.

## How to migrate

If you have a project consuming upstream Microsoft Coyote, switch to InterleaveX by updating only your NuGet references:

```diff
- <PackageReference Include="Microsoft.Coyote" Version="1.7.11" />
+ <PackageReference Include="InterleaveX" Version="..." />
```

Or for the granular packages:

```diff
- <PackageReference Include="Microsoft.Coyote.Core"   Version="1.7.11" />
- <PackageReference Include="Microsoft.Coyote.Actors" Version="1.7.11" />
- <PackageReference Include="Microsoft.Coyote.Test"   Version="1.7.11" />
+ <PackageReference Include="InterleaveX.Core"        Version="..." />
+ <PackageReference Include="InterleaveX.Actors"      Version="..." />
+ <PackageReference Include="InterleaveX.Test"        Version="..." />
```

For the global CLI tool:

```pwsh
# Uninstall the upstream Coyote tool if previously installed
dotnet tool uninstall --global Microsoft.Coyote.CLI

# Install the InterleaveX CLI
dotnet tool install --global InterleaveX.CLI
```

Note: if your code uses strong-name pinning (e.g., `[InternalsVisibleTo("Microsoft.Coyote, PublicKey=...")]` referencing the old key, or a specific signed-binding redirect), you will need to repin against the InterleaveX public-key token.

## Acknowledgements

InterleaveX would not exist without Microsoft Coyote. We are deeply grateful to the Microsoft Coyote team for:

- The original research and design of systematic concurrency testing.
- The implementation of the IL rewriter, the actor runtime, and the testing engine.
- Releasing the project under the permissive MIT license, which makes this fork — and many other downstream uses — possible.

The full Microsoft copyright and MIT license are preserved in this repository at [`LICENSE`](https://github.com/tradingfuturo/interleavex/blob/main/LICENSE).

## Where to ask questions

- For the **original Coyote design, research, or upstream releases**: refer to the [Microsoft Coyote repository](https://github.com/microsoft/coyote).
- For **InterleaveX-specific maintenance, bugs, or fork-tracked issues**: file an issue against the InterleaveX fork repository (URL pending).
