# Roadmap

> Phased delivery plan for daisi-broski. Each phase is independently shippable — you can stop at any phase and have a useful tool.
> [Architecture](architecture.md)

---

## Current state

**Phase 0, 1, and 4 are complete.** Phase 4 landed out of order — ahead of phases 2 and 3 — because a sandboxed phase-1 engine is immediately useful for scraping, link extraction, and preview generation, while phase 2 (CSSOM) is mostly plumbing that doesn't pay off until phase 3 (JS) is in. Phase 3 is the next major milestone; phase 2 will likely be absorbed into the front of phase 3 rather than shipping as its own unit.

**Combined test suite: 180/180 passing** (152 engine + 12 IPC codec + 7 Job Object + 4 sandbox integration + 5 CLI smoke).

What works today from a clean clone:

```
$ daisi-broski fetch https://news.ycombinator.com --select ".titleline > a"
200 OK https://news.ycombinator.com/ (35118 bytes, utf-8)
How to breathe in fewer microplastics in your home
... 29 more ...
30 match(es)
```

The full pipeline runs inside a `Daisi.Broski.Sandbox.exe` child process under a Win32 Job Object (256 MiB memory cap, kill-on-close, die-on-unhandled-exception, UI restrictions). `--no-sandbox` falls back to in-process execution.

---

## Phase 0 — Scaffolding ✅

- Repo created, .NET 10 conventions set up.
- Architecture and roadmap docs written.
- LICENSE, `.gitignore`, `global.json`, `Directory.Build.props` in place.

## Phase 1 — Network + HTML parser + DOM (no JS) ✅

**Goal:** fetch a URL, parse it, return a queryable DOM snapshot. No script execution.

**Ship gate achieved:**

```
$ daisi-broski fetch https://news.ycombinator.com --select ".titleline > a"
```

returns 30 story links, identical to what Chrome sees.

### Shipped

- **`Daisi.Broski.Engine`** core library:
  - `Net.HttpFetcher` — `HttpClient` facade with manual redirect following (capped), per-session `CookieContainer`, Chromium User-Agent default, streaming response size cap (50 MiB default). Decompression (gzip / deflate / brotli) via `SocketsHttpHandler`.
  - `Html.EncodingSniffer` — WHATWG encoding sniffing: BOM → `Content-Type` charset → `<meta>` prescan of the first 1024 bytes → UTF-8 fallback.
  - `Html.Tokenizer` — WHATWG HTML5 state machine, phase-1 subset: data, tag open/close/name, all three attribute value forms, self-closing, comments, DOCTYPE name, RAWTEXT / RCDATA / ScriptData for `<script>` / `<style>` / `<title>` / `<textarea>` / `<noscript>` / `<iframe>` / `<noembed>` / `<noframes>` / `<xmp>`.
  - `Html.HtmlEntities` — character-reference decoder: ~120 named entities covering >99% of real-world usage, decimal and hex numeric references, WHATWG Windows-1252 fixup for code points 0x80-0x9F, surrogate / out-of-range → U+FFFD.
  - `Html.HtmlTreeBuilder` — WHATWG insertion-mode state machine, phase-1 subset: Initial → BeforeHtml → BeforeHead → InHead → AfterHead → InBody → Text → AfterBody → AfterAfterBody. Implicit html/head/body synthesis, implicit `<p>` close on block-level elements, implicit close of same-named list / row / option / dd / dt, void element handling, character-run merging into single `Text` nodes, simplified "pop until matching name" for misnested end tags.
  - `Dom.{Node, Element, Document, Text, Comment, DocumentType}` — doubly-linked-list-backed tree with `ChildNodes`, sibling / parent pointers, `ownerDocument` adoption on attach, cycle-safe `AppendChild`, `getElementById` / `getElementsByTagName` / `getElementsByClassName`.
  - `Dom.Selectors.{SelectorParser, SelectorMatcher}` — CSS Selectors Level 4 pragmatic subset: type / universal / id / class / attribute (all 7 match operators + case-insensitive flag), compound, all four combinators, selector lists, pseudo-classes (`:first-child`, `:last-child`, `:only-child`, `:first-of-type`, `:last-of-type`, `:only-of-type`, `:nth-child(An+B)` and friends, `:root`, `:empty`, `:not`). Right-to-left matching via `SelectorMatcher.Matches`. Wired onto `Node.QuerySelector` / `Node.QuerySelectorAll`, `Element.Matches` / `Element.Closest`.
  - `PageLoader` — thin end-to-end glue: `HttpFetcher` → `EncodingSniffer` → `HtmlTreeBuilder` → `Document`.
- **`Daisi.Broski.Cli`** — command-line driver: `daisi-broski fetch <url> [--select <css>] [--html] [--ua <s>] [--max-redirects N] [--no-sandbox]`. Since phase 4, the default path spawns a sandbox child.

### Deliberately deferred (documented in the relevant class doc comments)

- **Tokenizer:** CDATA sections, DOCTYPE public/system identifiers, full script-data escape sub-states (legacy HTML3/IE compat), legacy no-semicolon named entities.
- **Tree builder:** table insertion modes (table tags parse as regular elements — wrong structure for malformed tables, works for well-formed ones), form element association, template elements, SVG/MathML foreign content, frameset, quirks mode, full adoption agency (misnested tag patterns like `<b><i></b></i>` give a different tree than Chrome).
- **DOM:** the original phase-1 sketch mentioned `Attr` and `NodeList` as distinct types. They were not implemented. Attributes are stored as an ordered `List<KeyValuePair<string, string>>` on `Element` (iteration order is preserved for future JS API compatibility), and node collections are exposed as `IReadOnlyList<Node>`. A proper `Attr` node and a live `NodeList` will be added in phase 3c when the JS DOM bridge needs them.
- **Selectors:** pseudo-elements (`::before`, `::after`), `:has` / `:is` / `:where`, `:hover` / `:focus` (no events / state), namespace prefixes.
- **Encoding:** `windows-1252`, `shift_jis`, `gbk`, and other legacy encodings aren't registered — unknown encoding names fall back to UTF-8 with mojibake. All modern web content is UTF-8.
- **html5lib conformance suite:** the original phase-1 plan called for vendoring the html5lib `.dat` test vectors and hitting >90% pass rate on the tokenizer and tree-construction suites. This was not done. Phase 1 ships with ~100 hand-written xUnit tests covering the same surface, which is sufficient for the ship-gate demo but is not an objective measure of spec conformance. Adding html5lib is a cheap follow-up — the tests are text files, no code needed to vendor them, only a test runner that iterates them.

### Design decisions captured

- [DD-01 Regex engine](design-decisions.md#dd-01--regex-engine) — BCL as phase-3a placeholder, hand-written NFA by 3c.
- [DD-05 JS heap and GC strategy](design-decisions.md#dd-05--js-heap-and-gc-strategy) — .NET GC in 3a/3b, refactor to tagged-union struct in 3c, decide arena vs. pool only after phase-7 profiling.

## Phase 2 — CSSOM + Web APIs (still no JS)

**Goal:** enough CSSOM and host-API plumbing that the JS engine has somewhere to attach.

- Full cascade: specificity, `!important`, inheritance, `var()`, `calc()`, media queries.
- `getComputedStyle` returning declared values (layout-dependent ones stubbed).
- Event dispatch system: `EventTarget`, `addEventListener`, bubbling/capturing, `CustomEvent`.
- Host API seams for the Web APIs that phase 3 will need (`setTimeout`, `console`, `fetch` — the C# side is ready, the JS side is stubbed).

**Ship gate:** unit tests for cascade + selector match pass against a reasonable WPT subset.

**Current status:** not started. Likely absorbed into the front of phase 3 — the cascade and event dispatch are useful only once scripts can query computed styles and dispatch events, so building them in isolation is plumbing for phase 3 with no independent payoff.

## Phase 3 — JavaScript engine

**This is the single largest phase.** See [js-engine.md](js-engine.md) (planned) for the detailed breakdown. Three sub-phases:

### Phase 3a — ES5 core

- Lexer, parser, AST.
- Bytecode compiler + stack VM.
- Built-ins: `Object`, `Function`, `Array`, `String`, `Number`, `Boolean`, `Math`, `Date`, `RegExp`, `Error`, `JSON`, `arguments`.
- Prototypes, `this`, closures, `try`/`catch`, strict mode.
- ECMA regex engine (or BCL fallback — see [DD-01](design-decisions.md#dd-01--regex-engine)).
- Event loop (task queue + microtask queue, HTML spec semantics).
- Wire up `console`, `setTimeout`, `setInterval` as the first Web APIs backed by JS.

**Ship gate:** test262 ES5 subset >80% pass.

### Phase 3b — ES2015 core

- `let`/`const` with temporal dead zone, block scoping.
- Arrow functions, classes (including static fields, `super`), template literals.
- Destructuring, default params, rest/spread.
- `Symbol`, iterators, `for..of`, generators.
- ESM module loader (host-provided resolver; modules come from the network via `fetch`).
- `Map`, `Set`, `WeakMap`, `WeakSet`, `Promise`, typed arrays, `ArrayBuffer`, `DataView`.

**Ship gate:** test262 ES2015 subset >70% pass, plus a real React app renders its initial content.

### Phase 3c — ES2017+ and the DOM bridge

- `async`/`await`, optional chaining, nullish coalescing, logical assignment.
- `Proxy`, `Reflect` minimum viable (enough for Vue 3 reactivity to not throw).
- `BigInt` basic ops.
- **DOM bridge:** every DOM node is reachable from JS via host objects. `document.querySelector` returns a JS value that routes property access back to the C# DOM. Events dispatched from JS mutate C# state and vice versa.
- `fetch`, `Request`, `Response`, `Headers`, `AbortController`.
- `URL`, `URLSearchParams`, `TextEncoder`, `TextDecoder`, `atob`, `btoa`.
- `crypto.getRandomValues`, `crypto.randomUUID`.

**Ship gate:** load `news.ycombinator.com` with scripts enabled, run them, and confirm the DOM after script execution matches what Chrome produces within a small tolerance.

**Current status:** not started. This is the next major milestone.

## Phase 4 — Sandbox host ✅ (infrastructure) / ⏸ (full ship gate blocked on phase 3)

**Goal:** everything phase 1–3 does, but in a child process with kernel-enforced resource limits.

**What ships today:**

```csharp
await using var session = BrowserSession.Create();
var nav = await session.NavigateAsync(new Uri("https://example.com"));
var links = await session.QuerySelectorAllAsync("a[href]");
```

…runs the full phase-1 engine in a `Daisi.Broski.Sandbox.exe` child process under a Win32 Job Object. The host process never parses HTML, runs selectors, or touches any untrusted input. `daisi-broski fetch` uses this path by default.

**Ship-gate nuance:** the original phase-4 ship gate said the sandbox should "run a JS-heavy site inside and return a DOM snapshot." The sandbox *infrastructure* is complete (spawn, Job Object, IPC, navigate + query round-trip, crash capture) and works against any server-rendered page. But it literally cannot run JS-heavy sites yet because there is no JavaScript engine — that's phase 3. Once phase 3 lands, the existing sandbox plumbing picks it up for free; no additional sandbox work is required.

### Shipped

- **`Daisi.Broski.Ipc`** — shared protocol library. No dependency on the engine or Win32:
  - `IpcMessage` envelope: Request / Response / Notification, JSON-RPC 2.0 shape without the version field, payloads as `JsonElement` so the envelope codec doesn't depend on any DTO.
  - `IpcCodec` — length-prefixed UTF-8 JSON framing over any `Stream`. Big-endian u32 length prefix, 64 MiB max frame size, clean EOF detection, rejects truncated / oversize / malformed frames before allocation.
  - Phase-1 DTOs: `NavigateRequest` / `NavigateResponse` (with opt-in `IncludeHtml`), `QueryAllRequest` / `QueryAllResponse` / `SerializedElement`, `CloseRequest` / `CloseResponse`, `NavigationCompletedNotification` / `NavigationFailedNotification`.
- **`Daisi.Broski`** — host library (Windows-only):
  - `JobObject` — managed wrapper over `CreateJobObject` / `SetInformationJobObject` / `AssignProcessToJobObject` / `QueryInformationJobObject` with a `SafeHandle` that guarantees close-on-finalize. Configurable via `JobObjectOptions`: `ProcessMemoryLimitBytes` (default 256 MiB), `KillOnJobClose` (default true), `DieOnUnhandledException` (default true), `RestrictUI` (default true — blocks desktop, clipboard, global atoms, handles). `Win32/NativeMethods.cs` holds the P/Invoke declarations + struct layouts.
  - `SandboxLauncher` — creates two `AnonymousPipeServerStream`s (bidirectional IPC), spawns `Daisi.Broski.Sandbox.exe` with the inherited client-handle strings on the command line, assigns the child to a fresh `JobObject`, returns a `SandboxProcess`. `ResolveDefaultSandboxPath` handles development and deployment layouts, and validates the apphost `.exe` is accompanied by its managed `.dll` to avoid the MSBuild half-copy trap.
  - `SandboxProcess` — host-side handle: `SendRequestAsync` with monotonically-increasing id correlation, stderr draining (essential for diagnosing child crashes — without it the failure mode is indistinguishable from "child closed its pipe"), `DisposeAsync` with best-effort clean close + Job-Object-enforced kill.
  - `BrowserSession` — the public phase-1 API. `Create()`, `NavigateAsync(url, userAgent, maxRedirects, includeHtml, ct)`, `QuerySelectorAllAsync(selector, ct)`, `DisposeAsync()`.
- **`Daisi.Broski.Sandbox`** (output `Daisi.Broski.Sandbox.exe`) — the child process:
  - `Program.cs` — parses `--in-handle` / `--out-handle`, opens the inherited anonymous pipes, hands control to `SandboxRuntime`.
  - `SandboxRuntime` — single-threaded dispatch loop: reads `IpcMessage` frames, routes to `HandleNavigate` / `HandleQueryAll` / `HandleClose`, drives a long-lived `PageLoader`, serializes matched elements as `SerializedElement` for the response.
- **`Daisi.Broski.Cli`** integration — the CLI now uses `BrowserSession` by default; `--no-sandbox` falls back to in-process `PageLoader`. Non-Windows platforms degrade gracefully with a warning. The stderr status line marks in-process runs with `[no-sandbox]`.

### Deliberately deferred

- **AppContainer profile creation.** Job Object alone gives us memory, UI, and lifetime limits — enough for the phase-1 threat model. `CreateAppContainerProfile` + `SECURITY_CAPABILITIES` for additional integrity-level / filesystem / network isolation will land when real multi-site handling requires per-origin sandboxing.
- **`CreateProcess(CREATE_SUSPENDED)` atomic launch.** The current launcher uses `Process.Start` + `AssignProcessToJobObject`, leaving a ~few-ms race window where the child runs without the Job Object's memory cap. During that window the child only parses argv and opens inherited pipe handles (no network, no parsing), so the exposure is minimal. The stricter variant using native `CreateProcess` is documented in [architecture.md §5.8](architecture.md#58-sandboxing).
- **Crash-recovery respawn.** The host surfaces child-death errors via `SandboxException` but does not automatically respawn a fresh sandbox to continue the session; callers dispose and recreate `BrowserSession` themselves.
- **Handle-table for JS/DOM object ids.** The original phase-4 plan called for a handle table on the sandbox side so the host could refer to live JS objects and DOM nodes by opaque ids across the IPC boundary. Not implemented yet — the current IPC only passes serialized snapshots (`SerializedElement`). The handle-table is a phase-3c concern because the JS DOM bridge needs it to let scripts receive and mutate node references.
- **Cross-platform variants.** Phase 5 covers Linux (`unshare` + seccomp-bpf + cgroups v2) and macOS (`sandbox_init`).

## Phase 5 — Hardening and extended Web APIs

- `localStorage`, `sessionStorage` (file-backed per origin).
- `IndexedDB` minimum viable (a KV store with version upgrades).
- `WebSocket`.
- `XMLHttpRequest` (legacy but still used).
- `MutationObserver`, `IntersectionObserver` (real implementation, not stub).
- `FormData`, `Blob`, `File`, `FileReader`.
- Request interception API (host-side) for test scaffolding and ad blocking.
- Fuzzing harness against the parsers and IPC decoder.
- Cross-platform sandbox research: Linux (unshare + seccomp-bpf + cgroups v2), macOS (`sandbox_init`).

**Current status:** not started. The fuzzing and cross-platform sandbox items are prerequisites for running the engine in untrusted server environments; the Web API items are blocked on phase 3.

## Phase 6 — Layout, rendering, screenshots

Optional and heavyweight. Only if there's real demand.

- Box model, block/inline flow.
- Flexbox.
- Grid.
- Text layout (line breaking, bidi).
- Font loading (the one place we probably have to ship bundled fonts or shell out to the OS).
- Paint → raster buffer.
- `Screenshot` IPC command returns a PNG.
- `getBoundingClientRect` and friends return real values.

At this point daisi-broski is a real browser engine, not just a headless DOM runner. It will also be an order of magnitude more code.

## Phase 7 — Performance

- Bytecode VM optimizations: inline caches, shape-based property access, constant folding.
- JS heap arena allocator for short-lived values.
- AOT compilation of the sandbox child (`PublishAot`) — requires resolving reflection use in built-ins.
- Parallel parsing (HTML parser on one thread, CSS parser on another).
- Incremental style recalc.

---

## What we're explicitly NOT doing, ever

- **Shipping a JIT.** The attack surface is too large for a sandboxed runtime with our threat model. Interpreter only.
- **Becoming a GUI browser.** No chrome, no tabs UI, no address bar. This is a programmable engine.
- **Supporting IE-era legacy quirks beyond what WHATWG requires.** If it's not in the living standard, we don't implement it.
- **Running plugins.** No Flash, no ActiveX, no NPAPI, no PPAPI. Ever.
