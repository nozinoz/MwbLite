# winget manifest for MwbLite

These are templates for the three files of a [winget](https://learn.microsoft.com/windows/package-manager/)
manifest, so people can install and update MwbLite with `winget install nozinoz.MwbLite`:

| File | Holds |
|---|---|
| `nozinoz.MwbLite.yaml` | package id and version |
| `nozinoz.MwbLite.installer.yaml` | the Setup download (GitHub Release), its SHA-256, silent-install facts |
| `nozinoz.MwbLite.locale.en-US.yaml` | name, publisher, licence (MIT), description, tags |

The installer is `MwbLite-Setup-<version>.exe`, a WiX Burn bundle (`InstallerType: burn`), machine scope, x64. It asks
for administrator approval itself and installs the .NET Desktop Runtime first when the PC needs it. winget runs it as
`MwbLite-Setup-<version>.exe /quiet` for a silent install, and a newer Setup upgrades an installed copy in place
(`UpgradeBehavior: install`), which is how `winget upgrade` works too.

## Make the manifest for a release

1. Build and publish the release as usual (`build.cmd`, then `tools\publish-release.ps1`). The manifest points at the
   release asset, so the release must exist.
2. Run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-winget-manifest.ps1
   ```

   It reads the version from `Directory.Build.props` and the Setup's SHA-256 from `dist\SHA256SUMS.txt`, writes the
   three files to `out\winget\<version>\`, and runs `winget validate` on them if winget is installed. Use `-Download`
   to hash the file that is actually attached to the release (do this if you rebuilt after publishing, since two
   builds of the same version need not give identical Setup files), or `-Sha256` to give the hash yourself.

## Submit it (not done by the scripts)

winget's community repository is [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). A submission is a
pull request that adds the three files under `manifests/n/nozinoz/MwbLite/<version>/`. Automated checks then
validate the manifest, download the installer, scan it and test-install it; a moderator merges it when they pass.
Allow a few days for the first one.

**By hand (also needed for the first version):**

1. Fork `microsoft/winget-pkgs` on GitHub and clone your fork.
2. Create the folder `manifests\n\nozinoz\MwbLite\<version>\` and copy the three files from `out\winget\<version>\`
   into it.
3. Run `winget validate --manifest manifests\n\nozinoz\MwbLite\<version>` (and, with administrator rights and
   `winget settings --enable LocalManifestFiles`, `winget install --manifest <that folder>` to try it).
4. Commit on a branch named like `nozinoz.MwbLite-<version>` with the message `New version: nozinoz.MwbLite version
   <version>` (the first time: `New package: nozinoz.MwbLite version <version>`), push it, and open a pull request to
   `microsoft/winget-pkgs`. Fill in the checklist in the PR template.

**With [wingetcreate](https://github.com/microsoft/winget-create)** (updates after the first version): 

```powershell
wingetcreate update nozinoz.MwbLite --version <version> --urls https://github.com/nozinoz/MwbLite/releases/download/v<version>/MwbLite-Setup-<version>.exe --submit
```

It downloads the Setup, fills in the hash, copies the other fields from the previous version and opens the pull
request (it asks you to sign in to GitHub with a token the first time). The reviewers may ask for changes in the PR;
push them to the same branch.

Notes:

- The package id (`nozinoz.MwbLite`) can't be changed after the first version is merged.
- Every release needs its own pull request, and the Setup behind each URL must never change afterwards: winget checks
  the hash, so replacing a release asset breaks installs until the manifest is updated.
- The Setup isn't code-signed. winget accepts that, but SmartScreen warns when the file runs, and the automated checks
  can flag unsigned installers more often. Signing (see `build.ps1 -CertThumbprint`) avoids both.
