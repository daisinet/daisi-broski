# daisi-broski
**Bro**wse And **Ski**m = Broski

**A native C# headless web browser, from scratch.**

daisi-broski is a from-scratch web browser engine written entirely in C# on top of the .NET 10 Base Class Library. It fetches real websites over HTTP/HTTPS, parses HTML5 into a queryable DOM, runs CSS selectors against it, and serves the whole pipeline through a sandboxed child process whose memory, handles, and network access are bounded by the Windows kernel.

No third-party NuGet packages in the product code. No Chromium, no WebKit, no V8, no AngleSharp, no Jint. Everything ships in this repo.

> **Why?** Because every embeddable browser in the .NET world is either a 300 MB wrapper around Chromium, or a brittle DOM-only parser that can't run JavaScript. We want something smaller, scriptable, memory-safe, and actually ours.

## Status

**Phases 0, 1, 3a, 3b, and 4 are shipped. Phase 3c is in progress — slices 3c-1 through 3c-8 land the ES2020 short-circuit operators, the JS ↔ DOM bridge, the WHATWG web-primitive built-ins (`URL`, `TextEncoder`, `atob`/`btoa`), DOM events with full capture/target/bubble phases, `crypto.getRandomValues` / `crypto.randomUUID`, `fetch` + `Headers` + `Request` + `Response`, `Proxy` + `Reflect` (enough for Vue 3 / MobX / signal-style reactivity), and `element.innerHTML` read/write (round-trips through the phase-1 HTML tree builder and a new HTML serializer).**

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

**Not yet:** JavaScript execution, full CSS cascade / `getComputedStyle`, event dispatch, layout, rendering, screenshots, `localStorage` / `IndexedDB` / `WebSocket`. See [docs/roadmap.md](docs/roadmap.md) for the phased plan.

**Combined test suite: 1332/1332 passing.** Phase 3b (ES2015+) is complete; phase 3c is underway. All engine, DOM, selector, and JS tests run in under a few seconds; the sandbox and CLI integration tests spawn real child processes against a local `HttpListener` fixture.

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

## Run the tests

```
dotnet test src/dotnet/Daisi.Broski.slnx
```

Some tests are Windows-only and skip cleanly on other platforms (JobObject tests, sandbox integration tests, CLI smoke tests).

## License

DAISI Community License v1.0 — see [LICENSE](LICENSE).
