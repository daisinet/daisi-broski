# daisi-broski
**Bro**wse And **Ski**m = Broski

**A native C# headless web browser, from scratch.**

daisi-broski is a from-scratch web browser engine written entirely in C# on top of the .NET 10 Base Class Library. It fetches real websites over HTTP/HTTPS, parses HTML5 into a queryable DOM, runs CSS selectors against it, and serves the whole pipeline through a sandboxed child process whose memory, handles, and network access are bounded by the Windows kernel.

No third-party NuGet packages in the product code. No Chromium, no WebKit, no V8, no AngleSharp, no Jint. Everything ships in this repo.

> **Why?** Because every embeddable browser in the .NET world is either a 300 MB wrapper around Chromium, or a brittle DOM-only parser that can't run JavaScript. We want something smaller, scriptable, memory-safe, and actually ours.

## Status

**Phases 0, 1, 3a, 3b, 3c, and 4 are shipped. Phase 3 is complete — phase 3c landed the ES2020+ sugar (`?.`, `??`, `&&=` / `||=` / `??=`), the JS ↔ DOM bridge with `innerHTML` / `outerHTML`, the DOM event system with full capture/target/bubble phases, the WHATWG web-primitive built-ins (`URL`, `URLSearchParams`, `TextEncoder`/`TextDecoder`, `atob`/`btoa`), `fetch` + `Headers` + `Request` + `Response`, `Proxy` + `Reflect.*`, `crypto.getRandomValues` / `crypto.randomUUID` / `crypto.subtle.digest`, `AbortController` / `AbortSignal` (including `timeout` and `any`), `BigInt` with literal syntax + full arithmetic, and regex literals (`/pattern/flags`) with `RegExp` + the regex-aware `String.prototype` surface. The next major milestone is phase 5 (sandbox hardening / cross-platform).**

What works today, from a clean clone:

```
$ daisi-broski fetch https://news.ycombinator.com --select ".titleline > a"
200 OK https://news.ycombinator.com/ (35118 bytes, utf-8)
How to breathe in fewer microplastics in your home
Cirrus Labs to join OpenAI shut down Circus CI on Monday, June 1, 2026
... 28 more ...
30 match(es)
```

The entire pipeline — network, encoding detection, HTML tokenizer, tree builder, DOM, CSS selectors — runs inside a `Daisi.Broski.Sandbox.exe` child process under a Win32 Job Object with a 256 MiB memory cap, kill-on-close, die-on-unhandled-exception, and UI restrictions. The host process never parses HTML, runs selectors, or touches any untrusted input. Pass `--no-sandbox` for in-process execution against trusted URLs only.

**Phase 3a complete.** The JS **lexer**, **parser**, **bytecode compiler + stack VM**, full ES5 semantics (objects / arrays / member access, functions / closures / `this` / `new` / `instanceof`, control flow, exception handling), the complete **built-in library** (`Array`/`String`/`Object`/`Math`/`Error`/`Number`/`Boolean`/`Function.prototype`/`JSON`/`Date` + globals), and the host-side **event loop** with `console`, `setTimeout`, `setInterval`, and `queueMicrotask` are all shipped. The pipeline runs real scripted programs end-to-end:

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

**Phases 0–6 are shipped.** JavaScript (full ES5 + ES2020+ sugar), CSS cascade with `getComputedStyle` + `var()` substitution, event dispatch, block / flex / grid / inline-flow layout, paint (with alpha-blended backgrounds + per-side borders + linear gradients), BCL-only PNG and baseline-JFIF JPEG decoders (with a shared `ImageDecoder.TryDecode` dispatcher), image fetching, inline SVG rendering (path / rect / circle / ellipse / polygon with viewBox scaling), text rendering via a bundled 5×7 bitmap font plus an embedded Roboto TTF for CSS-font fallback, and the full web-primitive surface (`localStorage` / `IndexedDB` / `WebSocket` / `XMLHttpRequest` / `MutationObserver` / `FileReader` / request interception) all land before or during phase 6. The `daisi-broski screenshot <url>` CLI produces a real PNG of what the engine rendered.

**Not yet:** full font metrics (system fonts — we ship a bitmap font and an embedded Roboto fallback instead), WebP decode, progressive JPEG, radial / conic gradients, transforms, opacity, border-radius, filters / shadows, stroke-to-path for thick strokes, explicit grid placement. See [docs/roadmap.md](docs/roadmap.md) for the phased plan.

### `daisi-broski-skim` — main-content extraction

A second CLI shipped as `Daisi.Broski.Skimmer` runs the full broski pipeline against a URL, picks the most likely article body via a Mozilla-Readability-style scoring pass (text density, link density, semantic landmarks, noise-class penalties), and emits the result as JSON or Markdown:

```
$ daisi-broski-skim https://en.wikipedia.org/wiki/JavaScript --format md --quiet
# JavaScript - Wikipedia

on _en.wikipedia.org_ • [source](https://en.wikipedia.org/wiki/JavaScript)

JavaScript (/ˈdʒɑːvəskrɪpt/), often abbreviated as JS, is a programming language...

## History

### Creation at Netscape
...
```

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

### `daisi-broski-surfer` — MAUI Blazor Hybrid reader app

A native Windows desktop app (`Daisi.Broski.Surfer`, .NET MAUI Blazor Hybrid, target `net10.0-windows10.0.19041.0`) wraps the Skimmer in a UI: address bar at the top, four content views (Reader / Markdown / JSON / Links) toggleable on the right, back button that walks the visit history. Type a URL, hit Enter — the Surfer fetches it through the local broski engine, runs `ContentExtractor.Extract`, and renders the chosen view in a `BlazorWebView`. The Links view turns every extracted outbound link into a one-click in-app navigation.

Build it from a clone:

```
dotnet workload install maui-windows  # one-time, if not already installed
dotnet build src/dotnet/Daisi.Broski.Surfer/Daisi.Broski.Surfer.csproj
dotnet run --project src/dotnet/Daisi.Broski.Surfer
```

Windows-only on the first ship — adding Android / iOS / MacCatalyst heads is a matter of dropping the right `Platforms/*` folders in and extending the `<TargetFrameworks>` list. Runs unpackaged (no MSIX bundle), so any user can launch it without a Microsoft Store install.

**Combined test suite: 1934/1934 passing.** Phase 4 is fully closed — the sandboxed child now runs the full phase-3 engine + the Skimmer; `BrowserSession.RunAsync` + `SkimAsync` expose the new capabilities as typed async methods; the child launches via `CreateProcessW(CREATE_SUSPENDED)` so Job Object assignment is atomic with process birth; mid-operation child crashes trigger a single-retry respawn; the **live handle table** lets the host hold references to JS / DOM objects inside the sandbox and drive interactive operations (`EvaluateAsync<T>`, `QuerySelectorHandleAsync`, `ElementHandle.ClickAsync` / `SetAttributeAsync`, `JsHandle.CallMethodAsync<T>`) across the IPC boundary. **Phase 5a shipped** — `localStorage` is now file-backed per origin via `BroskiOptions.StoragePath` (opt-in for embedded use) and default-on for sandbox runs; same-origin reloads see their own persisted state without re-running logins, and cross-origin navigation saves-then-reloads atomically. **Phase 5b shipped** — `Blob` / `File` / `FileReader` / `FormData` foundational binary-data types are in place, plus `Response.prototype.blob()`; `FileReader` schedules its `load` / `loadend` / `error` / `abort` events through the engine's event loop in spec order. **Phase 5c shipped** — `XMLHttpRequest` is now a real implementation backed by `JsEngine.FetchHandler` (shared with `fetch`), with full readyState transitions, `responseType` switching (`text` / `json` / `arraybuffer` / `blob`), and `multipart/form-data` encoding when a `FormData` body is sent. **Phase 5d shipped** — `MutationObserver` now fires real records via a per-`Document` `MutationDispatcher` hooked into every DOM mutation point; `childList` / `attributes` / `characterData` / `subtree` / `attributeFilter` / `attributeOldValue` / `characterDataOldValue` all honored, with records batched into one callback per microtask drain. **Phase 5e shipped** — `WebSocket` is backed by `System.Net.WebSockets.ClientWebSocket` through a pluggable `IWebSocketChannel` abstraction (host-side `JsEngine.WebSocketHandler` for stubs); cross-thread receives post back to the engine via the new thread-safe `JsEventLoop.PostFromBackground`, preserving the VM's single-threaded execution. **Phase 5f shipped** — minimum-viable `indexedDB` (open + version upgrade, object stores, put/add/get/delete/clear/count/getAll, transactions with `oncomplete`); persistence is JSON-per-database under `{StoragePath}/indexeddb/`, default-enabled for sandboxed runs. **Phase 5g shipped** — host-side `RequestInterceptor` on `HttpFetcherOptions` (and surfaced via `BroskiOptions.Interceptor`); intercepts every HTTP path the engine takes — page navigation, script tags, `fetch`, `XHR` — at one point. Returning a synthetic `InterceptedResponse` short-circuits the wire call (test mocks, ad blocking, offline replay); throwing surfaces as a hard exception (block-with-error semantics). **Phase 5h shipped** — fuzzing harness against the four parsers (HTML tokenizer, HTML tree builder, CSS selector parser, CSS selector matcher) and the IPC codec, ~12,000 random inputs per run from a fixed seed; failure means an unexpected exception type, an infinite loop, or unbounded allocation. Phase 3 is feature-complete plus a thick set of real-web shims (regex literals, two rounds of host APIs, ES2015+ prototype methods, DOM mixin methods, `Function` constructor, `self` / `globalThis` aliases, real-accessor RegExp prototype, recursion guard, Unicode identifiers + escapes, `new.target`, `for await`, async generator methods, ES2022 static blocks, NodeFilter / CSS globals, 25-class DOM-interface stub set, and the new **`Daisi.Broski.Skimmer`** main-content extractor + JSON / Markdown formatters). **Phase 3 ship gate met on the modern web**: the engine runs **100% of inline scripts cleanly** on svelte.dev (6/6), react.dev (3/3), nextjs.org (50/50), tailwindcss.com (185/185), nodejs.org (118/118), typescriptlang.org (5/5), vitejs.dev, preactjs.com, nuxt.com, remix.run, htmx.org, rust-lang.org, MDN, and more. Total: ~375 inline scripts executing across 13 real sites with zero runtime errors on the scripts we actually run. Heavyweight bundle sites partially run (Stripe 44/68, Anthropic 18/24, Figma 89/108, Linear 71/114, Cloudflare 21/24). All engine, DOM, selector, and JS tests run in under a few seconds; the sandbox and CLI integration tests spawn real child processes against a local `HttpListener` fixture.

## Design goals

- **Headless first.** No layout, no paint, no fonts. Network → DOM → (JS in phase 3) → scripted output. Visual rendering is phase 6 if real demand appears.
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

## Usage

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

**Embedded use (Windows-only until phase 5):**

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
