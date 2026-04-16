# npm packaging for @daisinet/broski

This folder is the maintainer-side source for the `@daisinet/broski` npm distribution. End users don't read this — they just run `npm install -g @daisinet/broski`.

## Layout

```
npm/
├── broski/                       ← main package (@daisinet/broski)
│   ├── package.json
│   ├── bin/broski.js             ← tiny shim that execs the platform binary
│   └── README.md
├── platforms/                    ← one sub-package per supported platform
│   ├── linux-x64/package.json    ← @daisinet/broski-linux-x64
│   ├── darwin-x64/package.json   ← @daisinet/broski-darwin-x64
│   ├── darwin-arm64/package.json ← @daisinet/broski-darwin-arm64
│   └── win32-x64/package.json    ← @daisinet/broski-win32-x64
├── publish.ps1                   ← one-shot maintainer publish script
├── install.sh                    ← curl-pipeable end-user installer (Unix)
├── install.ps1                   ← iex-pipeable end-user installer (Windows)
└── README.md                     ← this file
```

The on-disk binary keeps its `dotnet publish` filename `daisi-broski-skim[.exe]` inside each platform package — only the npm-exposed `broski` command is renamed (set in `bin/broski.js`'s lookup map).

## How users install

```
# primary path - works on every platform
npm install -g @daisinet/broski

# Unix one-liner (wraps the npm call + a clearer error if npm is missing)
curl -fsSL https://raw.githubusercontent.com/daisinet/daisi-broski/main/npm/install.sh | bash

# Windows one-liner
iwr -useb https://raw.githubusercontent.com/daisinet/daisi-broski/main/npm/install.ps1 | iex
```

npm resolves `optionalDependencies` with `os`+`cpu` filters, so only the binary for the user's machine actually downloads. The main package is ~1 KB of JS; the platform package adds ~30-32 MB depending on RID.

## How releases work

1. Pick a version string (semver).
2. From the repo root:
   ```powershell
   ./npm/publish.ps1 -Version 0.1.0
   ```
3. `publish.ps1` does:
   - Stamps `version` into all five `package.json` files (main + 4 platforms), and rewrites the main package's `optionalDependencies` version pins to match.
   - Runs `dotnet publish -c Release -r <rid> --self-contained true -p:PublishSingleFile=true` for each of the four platforms.
   - Copies the resulting single-file binary into `npm/platforms/<dir>/`.
   - Runs `npm publish` in each platform package first (so the main package's optionalDependencies resolve on install), then the main package last. Each `package.json` carries `publishConfig.access: public`, which is the access flag scoped packages need.
4. For a sanity-check without pushing to npm, pass `-DryRun` — same build + stamp, but each directory gets a local `.tgz` instead of a `npm publish` call.

The publish script is idempotent up to `npm publish`: rerun it and it re-builds everything. It's not idempotent AT `npm publish` — the registry rejects duplicate versions, so bump the version to re-release.

## Prereqs for a successful publish

- .NET 10 SDK (`dotnet --version` -> 10.x)
- Node 16+ and npm
- `npm login` with publish rights on the **`@daisinet`** scope (covers all five package names).

If the `@daisinet` scope isn't yours, claim it once on npmjs.com (free for an org), then `npm access grant` your account. Until that's done, `publish.ps1 -DryRun` works fine for local testing.

## Adding a new platform

1. Create `npm/platforms/<node-platform>-<node-arch>/package.json` using an existing one as a template. Set the correct `os` and `cpu` arrays — these are **Node.js** values (`linux` / `darwin` / `win32`, `x64` / `arm64`), not .NET RIDs.
2. Add the row to the `$platforms` array at the top of `publish.ps1`, mapping the .NET RID (e.g. `linux-arm64`) to the package dir + binary filename.
3. Add the package to `optionalDependencies` in `broski/package.json`.
4. Add the `platform-arch` -> `[package, binary]` entry to the `supported` map in `broski/bin/broski.js`.
