# Branding assets — placeholder pending replacement

The icons and logos in this folder (`icon_coyote_*.png`, `logo_coyote*.svg`,
`coyote_tutorial_intro.png`, etc.) are the original **Microsoft Coyote**
branding assets, retained here as placeholders for the InterleaveX fork.

These files should be replaced with InterleaveX-specific branding before
any public release of the fork. Until they are replaced:

- The `mkdocs.yml` config and `Common/build.props` `<PackageIcon>` reference
  these files by their original filenames so the docs site and NuGet
  packages continue to build without breakage.
- Anyone building or publishing fork artifacts should be aware that the
  icons embedded in the resulting NuGet packages are upstream Microsoft
  Coyote brand assets — the fork is using them under fair use as a
  placeholder, not as a claim of upstream identity.

When replacement assets are available, drop them in this folder using the
same filenames (or update `mkdocs.yml` and `Common/build.props` to point at
new filenames) and remove this README.
