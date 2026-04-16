# @daisinet/broski

Extract clean article content from any URL — HTML pages, Word documents, Excel workbooks, or PDFs — as JSON, Markdown, or reader-mode HTML.

Built on a from-scratch C# browser engine (no Chromium, no Node, no headless anything at runtime — the .NET self-contained binary weighs ~30-32 MB and runs a full DOM + JS engine + readability extractor in-process).

## Install

```bash
npm install -g @daisinet/broski
```

npm pulls a tiny JS launcher plus exactly one platform-specific binary package (`@daisinet/broski-linux-x64`, `-darwin-x64`, `-darwin-arm64`, or `-win32-x64`) — no postinstall downloads, no dependencies that need compiling.

## Usage

```bash
# Markdown output (great for LLMs)
broski https://en.wikipedia.org/wiki/JavaScript --format md

# Structured JSON
broski https://en.wikipedia.org/wiki/JavaScript --format json

# Office docs and PDFs work the same way
broski https://example.com/report.pdf --format md
broski https://example.com/budget.xlsx --format json

# Static-HTML sites are faster with scripting off
broski https://example.com --format md --no-scripts
```

Output goes to stdout. Pipe it wherever.

## Supported platforms

| OS | Arch | Notes |
|---|---|---|
| Linux | x64 | glibc-based distros |
| macOS | x64 | Intel Macs |
| macOS | arm64 | Apple Silicon |
| Windows | x64 | `daisi-broski-skim.exe` |

Linux ARM64 and FreeBSD are on the roadmap.

## License

See [LICENSE](https://github.com/daisinet/daisi-broski/blob/main/LICENSE) in the upstream repo.
