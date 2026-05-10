# Licensing and Project Notice

This repository hosts **InterleaveX**, a community-maintained fork of
[Microsoft Coyote](https://github.com/microsoft/coyote). The fork's mission
is to keep the Coyote systematic concurrency-testing toolchain alive on
current and upcoming .NET LTS releases for teams that depend on it in
production. See [`docs/overview/fork-rationale.md`](docs/overview/fork-rationale.md)
for the full story.

The fork is a **branding-only rebrand**: the product name, NuGet package IDs,
CLI command, and documentation read `InterleaveX`, but internal C# namespaces
(`Microsoft.Coyote.*`), assembly DLL names, and class names are retained
unchanged for source/binary compatibility with upstream consumers.

## Two licenses

This repository contains code under two different licenses.

## Original Microsoft Coyote code — MIT License

The original Microsoft Coyote source code in this repository is Copyright
(c) Microsoft Corporation and is licensed under the MIT License. The full
text is preserved in [`LICENSE`](LICENSE).

The MIT terms for the original Microsoft code are unchanged and continue to
apply to that code. In particular, the MIT copyright and permission notice
must be retained in all copies or substantial portions of that code.

## Modifications and additions — GNU General Public License v3.0

All modifications, additions, and new files contributed by
**InterleaveX maintainers** (Copyright (c) 2026 InterleaveX maintainers)
are licensed under the GNU General Public License version 3.0 (GPL-3.0).
The full text is in [`LICENSE-GPL`](LICENSE-GPL).

## Combined work

The combined/distributed work as a whole is offered under the terms of the
GPL-3.0. The MIT-licensed original Microsoft Coyote code is compatible with
GPL-3.0 and may be incorporated into a GPL-3.0 work; the MIT notice for that
code is preserved as required.

Anyone who receives a copy of this combined work has the rights granted by
the GPL-3.0 with respect to the work as a whole, including the right to
receive corresponding source and to redistribute under the same terms.

## How to identify which license applies to which file

- Files in this repository that originate from upstream Microsoft Coyote
  (including third-party packages under [`packages/`](packages/), each of
  which carries its own license) remain under their original licenses.
- New files authored by InterleaveX maintainers, and modifications to
  upstream files made by InterleaveX maintainers, are licensed under
  GPL-3.0.

When in doubt, check the file's own header comment or the git history for
authorship, and consult the relevant `LICENSE` file.

## Strong-name signing

InterleaveX is signed with its own strong-name key, not Microsoft's original
Coyote signing key. Downstream consumers that strong-name-pin against
Microsoft.Coyote will need to repin against the InterleaveX public-key token
when migrating. The signing key file lives at `Common/Key.snk` and is
generated/rotated by the fork maintainers.
