# Roadmap

> Phased delivery plan for daisi-broski. Each phase is independently shippable — you can stop at any phase and have a useful tool.
> [Architecture](architecture.md)

---

## Phase 0 — Scaffolding ✅

- Repo created, .NET 10 conventions set up.
- Architecture and roadmap docs written.
- LICENSE, `.gitignore`, `global.json`, `Directory.Build.props` in place.

## Phase 1 — Network + HTML parser + DOM (no JS)

**Goal:** fetch a URL, parse it, return a queryable DOM snapshot. No script execution.

- `Daisi.Broski.Engine` project skeleton.
- `Daisi.Broski.Engine.Net` — `HttpClient` facade with cookie jar, redirect cap, request interceptor hook.
- `Daisi.Broski.Engine.Html.Tokenizer` — full WHATWG tokenizer state machine.
- `Daisi.Broski.Engine.Html.TreeBuilder` — full insertion-mode state machine + adoption agency.
- `Daisi.Broski.Engine.Dom` — `Node`, `Element`, `Document`, `Text`, `Comment`, `Attr`, `NodeList`, basic `querySelector` / `querySelectorAll`.
- `Daisi.Broski.Engine.Css.Tokenizer` + `Parser` + `Selectors` (selector matching only; no cascade yet).
- html5lib `.dat` test vectors vendored and passing >90% in the tokenizer and tree-construction suites.

**Ship gate:** `daisi-broski fetch https://news.ycombinator.com --select ".storylink"` returns the same list of story links as `curl + a real parser`.

## Phase 2 — CSSOM + Web APIs (still no JS)

**Goal:** enough CSSOM and host-API plumbing that the JS engine has somewhere to attach.

- Full cascade: specificity, `!important`, inheritance, `var()`, `calc()`, media queries.
- `getComputedStyle` returning declared values (layout-dependent ones stubbed).
- Event dispatch system: `EventTarget`, `addEventListener`, bubbling/capturing, `CustomEvent`.
- Host API seams for the Web APIs that phase 3 will need (`setTimeout`, `console`, `fetch` — the C# side is ready, the JS side is stubbed).

**Ship gate:** unit tests for cascade + selector match pass against a reasonable WPT subset.

## Phase 3 — JavaScript engine

**This is the single largest phase.** See [js-engine.md](js-engine.md) (planned) for the detailed breakdown. Three sub-phases:

### Phase 3a — ES5 core

- Lexer, parser, AST.
- Bytecode compiler + stack VM.
- Built-ins: `Object`, `Function`, `Array`, `String`, `Number`, `Boolean`, `Math`, `Date`, `RegExp`, `Error`, `JSON`, `arguments`.
- Prototypes, `this`, closures, `try`/`catch`, strict mode.
- ECMA regex engine (or BCL fallback — see open questions in architecture.md).
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

## Phase 4 — Sandbox host

**Goal:** everything phase 1–3 does, but in a child process with kernel-enforced resource limits.

- `Daisi.Broski.Sandbox` project — console app whose `Main` accepts pipe handles and drives the engine.
- `Daisi.Broski` (host library) — public `BrowserSession` API, Win32 Job Object wrapper via P/Invoke.
- IPC protocol: length-prefixed JSON messages, bidirectional.
- Handle-table on the sandbox side for routing opaque JS/DOM ids.
- AppContainer profile creation + use (optional, opt-in).
- Crash recovery: on child death, host surfaces the error and can respawn.
- Integration tests that exercise the full host ↔ sandbox loop.

**Ship gate:** `BrowserSession.NavigateAsync(url)` from host code launches the sandbox, runs a JS-heavy site inside, returns a DOM snapshot, and the host never executes untrusted code.

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
