# daisi-broski
**Bro**wse And **Ski**m = Broski

**A native C# headless web browser, from scratch.**

daisi-broski is a from-scratch web browser engine written entirely in C# on top of the .NET 10 Base Class Library. It fetches real websites over HTTP/HTTPS, parses HTML5 into a queryable DOM, runs CSS selectors against it, and serves the whole pipeline through a sandboxed child process whose memory, handles, and network access are bounded by the Windows kernel.

No third-party NuGet packages in the product code. No Chromium, no WebKit, no V8, no AngleSharp, no Jint. Everything ships in this repo.

> **Why?** Because every embeddable browser in the .NET world is either a 300 MB wrapper around Chromium, or a brittle DOM-only parser that can't run JavaScript. We want something smaller, scriptable, memory-safe, and actually ours.

From a clean clone:

```
$ daisi-broski fetch https://news.ycombinator.com --select ".titleline > a"
200 OK https://news.ycombinator.com/ (35118 bytes, utf-8)
How to breathe in fewer microplastics in your home
Cirrus Labs to join OpenAI shut down Circus CI on Monday, June 1, 2026
... 28 more ...
30 match(es)
```

The entire pipeline — network, encoding detection, HTML tokenizer, tree builder, DOM, CSS selectors — runs inside a `Daisi.Broski.Sandbox.exe` child process under a Win32 Job Object with a 256 MiB memory cap, kill-on-close, die-on-unhandled-exception, and UI restrictions. The host process never parses HTML, runs selectors, or touches any untrusted input. Pass `--no-sandbox` for in-process execution against trusted URLs only.

The JS **lexer**, **parser**, **bytecode compiler + stack VM**, full ES5 semantics (objects / arrays / member access, functions / closures / `this` / `new` / `instanceof`, control flow, exception handling), the complete **built-in library** (`Array`/`String`/`Object`/`Math`/`Error`/`Number`/`Boolean`/`Function.prototype`/`JSON`/`Date` + globals), and the host-side **event loop** with `console`, `setTimeout`, `setInterval`, and `queueMicrotask` are all shipped. The pipeline runs real scripted programs end-to-end:

```csharp
var eng = new JsEngine();
eng.RunScript(@"
    // Classic functional pipeline
    var sum = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
        .filter(function (x) { return x % 2 === 0; })
        .map(function (x) { return x * x; })
        .reduce(function (a, b) { return a + b; }, 0);
    console.log('sum:', sum);   // 220

    // JSON + Date
    var event = {at: new Date(), msg: 'hello'};
    console.log(JSON.stringify(event));

    // setTimeout: callback sees persistent globals
    var greeting = 'hi';
    setTimeout(function () { console.log(greeting + ' from the event loop'); }, 10);
");
// eng.ConsoleOutput.ToString() →
//   sum: 220
//   {""at"":""2026-04-11T18:10:00.000Z"",""msg"":""hello""}
//   hi from the event loop
```

The JS surface covers every ES5 primitive operator, `var` hoisting, the full ES5 statement set, object and array literals with full member access, the `in` / `instanceof` operators, function declarations and expressions with full hoisting, nested functions, closures with captured environments, method calls with `this` binding, `new` with prototype chains, `arguments`, host-installed native functions, catchable internal errors, the **complete ES5 built-in library** (`Array.prototype` including callback methods, `String.prototype`, `Object`, `Math`, `Error` hierarchy with VM-originated `instanceof`, `JSON.parse`/`stringify`, `Function.prototype.call`/`apply`/`bind`, `Number`/`Boolean` + prototypes, `Date`), and a host-side event loop exposing `console.log`/`warn`/`error`/`info`/`debug`, `setTimeout`/`clearTimeout`, `setInterval`/`clearInterval`, and `queueMicrotask`. Call `engine.RunScript(source)` to run a script and drain the event loop in one step, or `engine.Evaluate(source)` + `engine.DrainEventLoop()` if you want to inspect state between.

### `daisi-broski-skim` — main-content extraction

A second CLI shipped as `Daisi.Broski.Skimmer` runs the full broski pipeline against a URL, picks the most likely article body via a Mozilla-Readability-style scoring pass (text density, link density, semantic landmarks, noise-class penalties), and emits the result as JSON or Markdown:

```
$ daisi-broski-skim https://daisi.ai --format json --quiet | head -8
{
  "url": "https://daisi.ai/",
  "title": "DAISI - Distributed AI Systems Inc",
  "byline": null,
  "publishedAt": null,
  "lang": "en",
  "siteName": "daisi.ai",
  "description": "DAISI is the fastest and most reliable distributed AI network on the planet.",
```

The library API is `ContentExtractor.Extract(document, url) → ArticleContent` plus `JsonFormatter.Format(article)` / `MarkdownFormatter.Format(article)`. Run `daisi-broski-skim --help` for the full CLI.

**Non-HTML inputs.** The skimmer also handles direct links to Word (`.docx`), Excel (`.xlsx`), and PDF (`.pdf`) documents. Detection is three-tiered: the response's Content-Type header wins, falling back to the body's magic bytes, and finally the URL's file extension. A match routes the bytes through a format-specific converter (`Daisi.Broski.Docs`) that emits a synthetic article-shaped HTML string; that HTML then flows through the exact same `HtmlTreeBuilder → ContentExtractor → JsonFormatter / MarkdownFormatter` pipeline the regular Web-page path uses. The two paths share a single formatter — a docx skim produces the same output shape as a scraped blog post.

```
$ daisi-broski-skim https://example.com/report.docx --format md --quiet
# Report.docx

Regular prose from the document shows up here, with **bold** and _italic_ runs preserved and `<a>` hyperlinks inlined. Lists, tables, and headings all translate cleanly.

$ daisi-broski-skim https://example.com/budget.xlsx --format json --quiet
$ daisi-broski-skim https://example.com/whitepaper.pdf --format md --quiet
```

Everything runs BCL-only — no third-party NuGet packages. OOXML (docx, xlsx) unpacks via `System.IO.Compression.ZipArchive` + `System.Xml.XmlReader`. PDF parsing is a from-scratch implementation of the PDF 1.7 spec §7 — a hand-rolled lexer, indirect-object parser, traditional + PDF 1.5+ cross-reference-stream readers, object-stream (`/Type /ObjStm`) expansion, five filters (`FlateDecode` with PNG-predictor support, `ASCIIHexDecode`, `ASCII85Decode`, `LZWDecode`, `RunLengthDecode`), a full `/ToUnicode` CMap parser (codespaceranges, bfchar, bfrange), and a content-stream interpreter for the text-showing operator set (`BT`/`ET`/`Tf`/`Tj`/`TJ`/`'`/`"`/`Td`/`TD`/`Tm`/`T*`) with CTM (`q`/`Q`/`cm`) tracking so every run's position is captured in a single consistent user-space frame. Font decoding covers both simple fonts (Type1/TrueType with `StandardEncoding` / `WinAnsiEncoding` / `MacRomanEncoding` + `/Differences` overlays, routed through an Adobe Glyph List table to Unicode) and composite Type 0 / CID fonts with 2-byte codes driven by the embedded `/ToUnicode` CMap. **Encryption** support handles the Adobe Standard Security Handler in V1 (RC4-40), V2 (RC4-128), and V4 (AES-128 via `/CFM /AESV2`) — the empty-user-password case, which is what Office, Adobe, and most enterprise doc-management systems produce by default when an author sets permissions without a real password. RC4 is inlined; AES uses the BCL. **Layout reconstruction**: positioned text runs flow through a layout analyzer (`PdfLayoutAnalyzer`) that clusters by y into rows, 1-D-linkage-clusters x-positions into column anchors, and emits markdown tables when it finds a rectangular grid of ≥3 rows / ≥2 columns with substantive content. This covers what Word 2016+, Chrome's PDF printer, LaTeX (including CJK content via CID fonts), Google Docs, and password-"protected" Office exports actually emit. **Known limitations:** tight-column two-column "actor : role" lists may fall inside our cluster tolerance and render as paragraphs instead of tables; composite fonts that rely on a predefined CMap (e.g., `GBK-EUC-H`) without shipping their own `/ToUnicode` produce empty text for their runs; and non-empty-password encryption needs a user-provided password which isn't yet wired through the CLI. See [docs/roadmap.md](docs/roadmap.md) for remaining work and [docs/design-decisions.md §DD-06](docs/design-decisions.md#dd-06--doc-conversion-representation-and-bcl-only-pdf) for the architecture.

### `daisi-broski-surfer` — MAUI Blazor Hybrid reader app

A native Windows desktop app (`Daisi.Broski.Surfer`, .NET MAUI Blazor Hybrid, target `net10.0-windows10.0.19041.0`) wraps the Skimmer in a UI: address bar at the top, four content views (Reader / Markdown / JSON / Links) toggleable on the right, back button that walks the visit history. Type a URL, hit Enter — the Surfer fetches it through the local broski engine, runs `ContentExtractor.Extract`, and renders the chosen view in a `BlazorWebView`. The Links view turns every extracted outbound link into a one-click in-app navigation.

Build it from a clone:

```
dotnet workload install maui-windows  # one-time, if not already installed
dotnet build src/dotnet/Daisi.Broski.Surfer/Daisi.Broski.Surfer.csproj
dotnet run --project src/dotnet/Daisi.Broski.Surfer
```

Windows-only on the first ship — adding Android / iOS / MacCatalyst heads is a matter of dropping the right `Platforms/*` folders in and extending the `<TargetFrameworks>` list. Runs unpackaged (no MSIX bundle), so any user can launch it without a Microsoft Store install.

## Design goals

- **Headless first.** Network → DOM → (JS in phase 3) → scripted output.
- **BCL only.** `HttpClient`, `SslStream`, `System.IO.Compression`, `System.Text.Json`, `System.IO.Pipes`, P/Invoke to Win32. Nothing else in product code. Test projects use xunit.v3.
- **Sandboxed by construction.** The engine runs in a child process under a Win32 Job Object with a hard memory cap, kill-on-close, UI restrictions, and (optionally, phase 5) an AppContainer SID. The host process talks to it over anonymous pipes using a length-prefixed JSON IPC protocol.
- **Works with most websites.** The bar for phase 1 is "modern server-rendered sites load and respond to CSS queries." The phase-3 bar will raise to "JS-heavy SPAs render their initial content and respond to scripted interaction." Not "pixel-perfect layout."
- **Embeddable.** The core engine is a library. A CLI and a host API are thin wrappers.

## Non-goals

- Pixel-perfect CSS layout or rendering (deferred to phase 6+).
- A GUI browser chrome. This is a programmable engine, not Firefox.
- Cross-platform sandboxing on day one. Windows first; Linux (`unshare` + seccomp-bpf + cgroups v2) and macOS (`sandbox_init`) are phase 5.
- Full ECMAScript spec compliance. Phase 3 targets the subset real sites actually use.
- A JavaScript JIT. Ever. Interpreter only — the attack surface is too large for our sandbox threat model.

## Repo layout

```
daisi-broski/
├── src/dotnet/
│   ├── Daisi.Broski.Engine/         ✅ Core engine: Net, Html, Dom, Dom.Selectors, PageLoader
│   ├── Daisi.Broski.Ipc/            ✅ Shared IPC protocol: IpcMessage, IpcCodec, phase-1 DTOs
│   ├── Daisi.Broski/                ✅ Host library: JobObject, SandboxLauncher, BrowserSession
│   ├── Daisi.Broski.Sandbox/        ✅ Child process: SandboxRuntime, IPC dispatch loop
│   ├── Daisi.Broski.Cli/            ✅ Command-line driver: daisi-broski fetch
│   ├── Daisi.Broski.Docs/           ✅ docx / xlsx converters + dispatch (PDF stub, parser WIP)
│   ├── Daisi.Broski.Skimmer/        ✅ daisi-broski-skim: article extractor (JSON / Markdown)
│   ├── Daisi.Broski.Surfer/         ✅ daisi-broski-surfer: MAUI Blazor Hybrid reader app (Windows)
│   ├── Daisi.Broski.slnx            Solution file
│   └── tests/
│       └── Daisi.Broski.Engine.Tests/ ✅ xunit.v3: 180 tests across Engine / Ipc / Sandbox / Cli
├── docs/
│   ├── architecture.md              System design
│   ├── roadmap.md                   Phased plan + current state
│   └── design-decisions.md          Long-form write-ups of non-trivial choices (DD-01, DD-05)
├── global.json                      .NET 10 SDK pin
├── Directory.Build.props            Shared MSBuild properties
├── LICENSE                          DAISI Community License v1.0
└── README.md
```

## CLI Usage

**Build:**

```
dotnet build src/dotnet/Daisi.Broski.slnx
```

**Fetch and print a title + counts:**

```
dotnet run --project src/dotnet/Daisi.Broski.Cli -- fetch https://example.com
```

```
200 OK https://example.com/ (528 bytes, utf-8)
title: Example Domain
links: 1
images: 0
scripts: 0
```

**Fetch and run a CSS selector:**

```
dotnet run --project src/dotnet/Daisi.Broski.Cli -- fetch https://news.ycombinator.com --select ".titleline > a"
```

**Fetch and dump the decoded HTML body:**

```
dotnet run --project src/dotnet/Daisi.Broski.Cli -- fetch https://example.com --html
```

**Options:**

| Flag | Purpose |
|---|---|
| `--select <css>` | Print matching elements' text content, one per line. |
| `--html` | Print the full decoded HTML body. |
| `--ua <string>` | Override the User-Agent header. |
| `--max-redirects N` | Cap the redirect chain (default 20). |
| `--no-sandbox` | Run in-process instead of spawning `Daisi.Broski.Sandbox.exe`. Use only for trusted URLs. |

**Embedded use (Windows-only):**

```csharp
using Daisi.Broski;

await using var session = BrowserSession.Create();

var nav = await session.NavigateAsync(new Uri("https://example.com"));
Console.WriteLine($"{nav.Status} {nav.FinalUrl}");
Console.WriteLine($"title: {nav.Title}");

var links = await session.QuerySelectorAllAsync("a[href]");
foreach (var link in links)
{
    Console.WriteLine($"{link.Attributes["href"]} → {link.TextContent}");
}
```

`BrowserSession.Create()` spawns `Daisi.Broski.Sandbox.exe` under a fresh Win32 Job Object; the session's `DisposeAsync` closes the pipes and the job, which terminates the child through `KILL_ON_JOB_CLOSE`.

**Interactive scripting via live handles:**

```csharp
await using var session = BrowserSession.Create();
await session.RunAsync(new Uri("https://example.com"), scriptingEnabled: true);

// Evaluate primitives directly.
var title = await session.EvaluateAsync<string>("document.title");

// Hold a live reference to a DOM element; mutations go through the
// real JS call path (listeners fire, observers run).
await using var btn = await session.QuerySelectorHandleAsync("button#go");
await btn!.SetAttributeAsync("data-state", "running");
await btn.ClickAsync();

// Or call any JS function on any reachable object.
await using var adder = await session.EvaluateHandleAsync("window.adder");
var sum = await adder!.CallMethodAsync<double>("add",
    new[] { IpcValue.Of(7.0), IpcValue.Of(35.0) });
```

Handles are opaque `long` ids minted by the sandbox and released on `DisposeAsync` (or on the next `NavigateAsync` / `RunAsync`, which clears the table wholesale).

## Run the tests

```
dotnet test src/dotnet/Daisi.Broski.slnx
```

Some tests are Windows-only and skip cleanly on other platforms (JobObject tests, sandbox integration tests, CLI smoke tests).

## License

DAISI Community License v1.0 — see [LICENSE](LICENSE).
