# daisi-broski

**A native C# headless web browser, from scratch.**

daisi-broski is a from-scratch web browser engine written entirely in C# on top of the .NET 10 Base Class Library. It loads real websites, parses HTML5 and CSS, executes JavaScript, and exposes a scriptable DOM — all inside a sandboxed child process whose memory, handles, and network access are bounded by the Windows kernel.

No third-party NuGet packages. No Chromium, no WebKit, no V8, no AngleSharp, no Jint. Everything ships in this repo.

> **Why?** Because every embeddable browser in the .NET world is either a 300 MB wrapper around Chromium, or a brittle DOM-only parser that can't run JavaScript. We want something smaller, scriptable, memory-safe, and actually ours.

## Status

**Planning.** See [docs/architecture.md](docs/architecture.md) and [docs/roadmap.md](docs/roadmap.md) for the design.

## Design goals

- **Headless first.** No layout, no paint, no fonts. Network → DOM → JS → scripted output. Visual rendering is a later phase.
- **BCL only.** `HttpClient`, `SslStream`, `System.IO.Compression`, `System.Text.Json`, `System.IO.Pipes`, P/Invoke to Win32. Nothing else.
- **Sandboxed by construction.** The engine runs in a child process under a Win32 Job Object with a hard memory cap, kill-on-close, UI restrictions, and (optionally) an AppContainer SID. The host process talks to it over a named pipe.
- **Works on most websites.** The bar is "modern JS-heavy sites render their initial content and respond to scripted interaction." Not "pixel-perfect layout."
- **Embeddable.** The core engine is a library. A CLI and a host API are thin wrappers.

## Non-goals

- Pixel-perfect CSS layout or rendering (deferred to phase 6+).
- A GUI browser chrome. This is a programmable engine, not Firefox.
- Cross-platform sandboxing on day one. Windows first; Linux (seccomp + namespaces) and macOS (Sandbox.kext) are follow-ups.
- Full ECMAScript spec compliance. We target the subset real sites actually use.

## Repo layout

```
daisi-broski/
├── src/
│   ├── Daisi.Broski/            # Core engine library (planned)
│   ├── Daisi.Broski.Sandbox/    # Child-process sandbox host (planned)
│   ├── Daisi.Broski.Cli/        # Command-line driver (planned)
│   └── Daisi.Broski.Tests/      # xUnit v3 tests (planned)
├── docs/
│   ├── architecture.md          # System design
│   ├── roadmap.md               # Phased plan
│   ├── js-engine.md             # JavaScript engine design (planned)
│   ├── html-parser.md           # HTML5 tokenizer/tree construction (planned)
│   └── sandbox.md               # Win32 Job Object + AppContainer design (planned)
├── global.json
├── Directory.Build.props
├── LICENSE
└── README.md
```

## License

DAISI Community License v1.0 — see [LICENSE](LICENSE).
