# Upstream provenance

MWB Lite is derived from Mouse Without Borders in Microsoft PowerToys (MIT licence).

- Repository: https://github.com/microsoft/PowerToys
- Commit: `cf45de4118c22dbe80a2aa55c6ea0935ea7e2b3b` (2026-10-21 11:28:38 +0800)
- Imported paths:
  - `src/modules/MouseWithoutBorders/App` → `src/App`
  - `src/modules/MouseWithoutBorders/MouseWithoutBorders.UnitTests` → `src/UnitTests`
  - `LICENSE`, `NOTICE.md` → repo root

The first commit in this repository is that import, byte-for-byte unmodified.
`git diff <baseline> -- <path>` shows every local change.
