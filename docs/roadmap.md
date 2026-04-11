# Roadmap

> Phased delivery plan for daisi-broski. Each phase is independently shippable — you can stop at any phase and have a useful tool.
> [Architecture](architecture.md)

---

## Current state

**Phase 0, 1, 3a, and 4 are complete. Phase 3b has started** with block-scoped `let`/`const` as its first slice. Phase 4 landed out of order — ahead of phases 2 and 3 — because a sandboxed phase-1 engine is immediately useful for scraping, link extraction, and preview generation, while phase 2 (CSSOM) is mostly plumbing that doesn't pay off until phase 3 is in. Phase 2 will likely be absorbed into phase 3b rather than shipping as its own unit.

**Combined test suite: 696/696 passing** (152 engine phase-1 + 12 IPC codec + 7 Job Object + 4 sandbox integration + 5 CLI smoke + 43 JS lexer + 69 JS parser + 51 JS VM + 38 JS objects + 34 JS functions + 25 JS control flow + 22 JS exceptions + 46 JS built-ins 6a + 41 JS built-ins 6b + 39 JS built-ins 6c + 20 JS Date 6d + 21 JS event loop 7 + 21 JS let/const 3b-1 + 21 JS arrows 3b-2 + 25 JS templates 3b-3).

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

### Phase 3a — ES5 core ✅

- **Lexer** ✅ — `Daisi.Broski.Engine.Js.JsLexer` and `JsTokenKind` / `JsToken`. Recognizes every ES5 keyword, the ES2015+ future-reserved keywords, decimal + scientific + hex number literals, single- and double-quoted string literals with the common escapes (`\n`, `\t`, `\r`, `\b`, `\f`, `\v`, `\0`, `\'`, `\"`, `\\`, `\xXX`, `\uXXXX`, line continuations), line and block comments (skipped, not emitted), and all ES5 punctuators including greedy long matches (`>>>=`, `===`, etc.). Deferred: regex literals (context-sensitive, needs parser cooperation), template literals (ES2015 — phase 3b), BigInt literals (phase 3c), Unicode identifiers beyond ASCII. 43 tests passing.
- **Parser + AST** ✅ — `Daisi.Broski.Engine.Js.JsParser` and the ESTree-shaped sealed-class hierarchy in `Ast.cs`. Covers every ES5 statement form (`var`/`function`/`if`/`while`/`do..while`/C-style `for`/`for..in`/`break`/`continue`/`return`/`throw`/`try`/`catch`/`finally`/`switch` with fall-through/`with`/`debugger`/labeled/block/empty/expression) and every ES5 expression form including the full operator-precedence table, right-associative assignment and conditional, member access, computed member access, function calls, `new` with and without arguments, array and object literals (with holes, trailing commas, reserved-word keys, and `get`/`set` accessors), and function expressions (named and anonymous). Implements automatic semicolon insertion including the restricted productions (`return`/`throw`/`break`/`continue`, postfix `++`/`--`) and the `in`-operator ambiguity in `for` headers. `let`/`const` are accepted (tagged for future block scoping); other ES2015 forms (arrow, class, template, destructuring) are rejected with a descriptive error. Regex literals are still deferred. 69 tests passing.
- **Bytecode compiler + stack VM (slice 3 — primitives and global control flow)** ✅ — `Daisi.Broski.Engine.Js.JsCompiler` walks the parser's AST into a `Chunk` of single-byte opcodes and the `JsVM` executes it on a value stack. `JsEngine` wraps the pipeline into `Evaluate(source)`, returning the completion value of the last top-level expression per ECMA §14. Scope covers: primitive literals; `var` with hoisted global declarations; assignment and compound assignment to identifiers; every unary / binary / logical / conditional / sequence / update operator; `typeof` (special-cased for undeclared identifiers); `if`/`else`, `while`, `do..while`, C-style `for`, `break`, and `continue` (unlabeled); ES §11.9 loose and strict equality, §11.8.5 relational comparison with string-string and numeric coercion, §9.5 / §9.6 `ToInt32` / `ToUint32` for bitwise ops and shifts, and §9.8.1 number-to-string formatting. Values are boxed .NET objects (DD-05 option A). 52 end-to-end tests.
- **Objects + arrays + member access (slice 4a)** ✅ — `JsObject` (prototype-aware property bag with virtual `Get`/`Set`/`Has`/`Delete`) and `JsArray` (subclass with dense `List<object?>` storage, numeric-index routing, virtual `length`, and `Array.prototype.toString`-equivalent `Join` for string coercion). Compiler emits `CreateObject` / `CreateArray` / `InitProperty` / `GetProperty`(`Computed`) / `SetProperty`(`Computed`) / `DeleteProperty`(`Computed`) / `In` / `Dup2` / `StoreScratch` / `LoadScratch` opcodes for object and array literals, dot and computed member access (including on the left of assignment, compound assignment, prefix and postfix update, and `delete`), and the `in` operator. Object literal keys follow the parser's normalized form (identifier / string / number all landing as strings). Reference equality for object identity via `===`. Array holes are approximated as explicit `undefined` (documented deferral). 38 end-to-end tests including nested object literals, compound member assignment, postfix update on computed indices, `in` on objects and arrays, array length truncation, and a word-counter dictionary iteration.
- **Functions + closures + `this` + `new` + `instanceof` (slice 4b)** ✅ — `JsEnvironment` (Dictionary-backed binding record with parent reference for chain lookup), `JsFunctionTemplate` (immutable compiled body with param names), and `JsFunction` (subclass of `JsObject` carrying a template plus captured env; every user function gets a fresh `prototype` object with `constructor` pointing back). The VM carries a call-frame stack, a current `_env` pointer, and a `_this` slot; the existing `LoadGlobal` / `StoreGlobal` / `DeclareGlobal` / `DeleteGlobal` opcodes now walk the env chain. New opcodes: `MakeFunction`, `Call` / `New` (u8 argc), `Return`, `Instanceof`, `LoadThis`, `Swap`. The compiler grew a frame stack — nested function bodies compile into their own `Chunk` — and hoists both `var`s and function declarations to the top of the enclosing function. Method calls bind `this` to the object via a `Dup` / `GetProperty` / `Swap` sequence. `new` allocates a fresh instance, links its prototype to `F.prototype` read live, runs the constructor, and substitutes the constructor's return value only if it is an object (ECMA §13.2.2). `arguments` binds to a `JsArray` of the raw argument values. Host-installed native functions work via a `NativeImpl` delegate. 34 end-to-end tests.
- **Remaining control flow — `for..in`, `switch`, labeled break/continue (slice 4c)** ✅ — `ForInStart` / `ForInNext` opcodes and an internal `ForInIterator` that snapshots enumerable own + inherited string keys (dedup by `HashSet<string>`, skipping `length` on arrays per spec). `for..in` supports identifier and `var identifier` LHS (member LHS is rare enough to defer). `switch` uses a dispatch-table layout: duplicate discriminant for each case test, `StrictEq` + `JumpIfTrue` to the case's entry label; entry labels do `Pop` (of the still-on-stack discriminant) + `Jump` to the body label; bodies are concatenated so fall-through happens by adjacency; default entry skips the `Pop` because it is only reached via fall-through or the no-match path (which pops first). Labeled break / continue route through a generalized `BreakTarget` context stack — each loop or switch pushes one with an optional label, and `break`/`continue` walks the stack looking for the matching (labeled) target. `continue` can only target a loop (never a switch); labeled blocks accept `break label;` so `foo: { if (x) break foo; ... }` works. 25 end-to-end tests.
- **Exception handling — `throw` / `try` / `catch` / `finally` (slice 5)** ✅ — New opcodes `PushCatchHandler` / `PushFinallyHandler`, `PopHandler`, `Throw`, `EndFinally`, `PushEnv` / `PopEnv`. The VM carries a handler stack and a pending-exception slot. `DoThrow` unwinds handlers and call frames to find the nearest catch or finally; internal errors (`ReferenceError`, `TypeError`) are raised via a `RaiseError` helper so script-level `try`/`catch` intercepts them. Uncaught throws escape as a .NET `JsRuntimeException` carrying the thrown `JsValue`. Catch parameter gets its own block-scoped env via `PushEnv`. Three compile layouts for the three try shapes; catch+finally installs a finally-only handler around the catch body. 22 end-to-end tests. **Deferred**: cross-`finally` escape semantics for `return` / `break` / `continue`.
- **Built-in library, slice 6a — global functions + Array + String** ✅ — `JsEngine` now owns well-known prototype objects (`ObjectPrototype`, `ArrayPrototype`, `StringPrototype`, `NumberPrototype`, `BooleanPrototype`) and installs built-ins at construction. The VM takes a `JsEngine` reference so `CreateArray` sets the array prototype and `GetProperty` / `GetPropertyComputed` on a string primitive look up `length`, integer indices, and prototype methods. `JsObject.SetNonEnumerable` keeps prototype methods out of `for..in` enumeration. Native built-ins signal catchable JS exceptions via `JsThrow.TypeError` / etc., which throw an internal `JsThrowSignal` that `InvokeFunction` catches and routes through `DoThrow`. **Shipped globals**: `parseInt`, `parseFloat`, `isNaN`, `isFinite`. **Shipped Array**: `Array` constructor (length or elements), `Array.isArray`, and prototype methods `push`, `pop`, `shift`, `unshift`, `slice`, `concat`, `join`, `indexOf`, `reverse`, `sort` (no compareFn), `toString`. **Shipped String**: `String` coercion constructor, `String.fromCharCode`, and prototype methods `charAt`, `charCodeAt`, `indexOf`, `lastIndexOf`, `slice`, `substring`, `substr`, `toLowerCase`, `toUpperCase`, `trim`, `split`, `concat`, `toString`. String primitives get `length` and integer indexing via the VM's primitive-property lookup. 46 end-to-end tests including `'hello'.split('').reverse().join('')`.
- **Built-in library, slice 6b — re-entrant VM + Object + Math + Error + callback Array methods** ✅ — `JsVM.Run` is refactored into a reusable `RunLoop(stopFrameDepth)` plus a `_halted` flag; `JsVM.InvokeJsFunction(fn, this, args)` lets native built-ins run a JS function synchronously by setting up a nested dispatch against a new frame and running until the frame is popped. A `_nativeBoundaries` stack records the frame depth below which `DoThrow` cannot unwind without crossing the current native call; when it hits one, it escapes via a `JsThrowSignal` that the outer `InvokeFunction` catches and re-routes through `DoThrow` at the higher level, after cleaning up abandoned frames / handlers and restoring the saved VM state. Native built-ins that need the VM use a new `JsFunction.NativeCallable` delegate alongside the existing `NativeImpl`; `InvokeFunction` dispatches to whichever is set. **Array callback methods**: `forEach`, `map`, `filter`, `reduce`, `reduceRight`, `every`, `some`, and `sort` with a `compareFn`. **Object**: constructor, `Object.keys`, `Object.create`, `Object.getPrototypeOf`, and `Object.prototype.hasOwnProperty` / `isPrototypeOf` / `propertyIsEnumerable` / `toString` / `valueOf`. **Math**: `PI` / `E` / `LN2` / `LN10` / `LOG2E` / `LOG10E` / `SQRT2` / `SQRT1_2` plus `abs` / `ceil` / `floor` / `round` / `sqrt` / `exp` / `log` / `sin` / `cos` / `tan` / `asin` / `acos` / `atan` / `sign` / `pow` / `atan2` / `min` / `max` / `random`. **Error hierarchy**: `Error`, `TypeError`, `RangeError`, `SyntaxError`, `ReferenceError`, `EvalError`, `URIError`; each has a prototype carrying `name` / `message` / `toString`, and the subclass prototypes chain to `Error.prototype`. The VM's `RaiseError` now uses the engine's error prototypes so internal errors (reading an undeclared variable, calling a non-function) produce objects that satisfy `e instanceof ReferenceError` / `e instanceof TypeError` from script. 41 end-to-end tests including `filter`/`map`/`reduce` pipelines, numeric `sort(compareFn)`, callback throws propagating through `forEach` to an outer `try`/`catch`, `Math.min()` / `Math.max()` argument-less edge cases, and the VM-originated `instanceof TypeError` check.
- **Built-in library, slice 6c — JSON + Function.prototype + Number/Boolean** ✅ — `BuiltinJson` ships `JSON.parse` (recursive-descent parser over the full JSON grammar with string escapes including `\uXXXX`, surrogate handling via raw UTF-16, scientific notation, and proper error reporting via a `SyntaxError`) and `JSON.stringify` (cycle detection via a reference-equality `HashSet`, NaN/Infinity → `"null"`, functions/undefined omitted from objects and rendered as `null` inside arrays, compact output only — `replacer` / `space` arguments deferred). `BuiltinFunction` installs `Function.prototype.call`, `apply`, and `bind` as `NativeCallable` implementations that thunk through `JsVM.InvokeJsFunction`; `bind` creates a fresh `NativeCallable` closing over the target function, bound `this`, and any prefix arguments, and merges them with the runtime args on each call. `JsEngine.FunctionPrototype` is the new `[[Prototype]]` for every `JsFunction` value — set at `MakeFunction` time for user functions and fixed up post-install for built-ins via a prototype-chain walk at engine construction. `BuiltinNumberBoolean` ships `Number` constructor + static `MAX_VALUE` / `MIN_VALUE` / `NaN` / `POSITIVE_INFINITY` / `NEGATIVE_INFINITY` / `EPSILON` / `MAX_SAFE_INTEGER` / `MIN_SAFE_INTEGER`, `Number.isNaN` / `isFinite` / `isInteger` statics, and prototype `toString(radix)` / `toFixed` / `valueOf`. Boolean gets a coercion constructor and `toString` / `valueOf` prototype methods. 39 end-to-end tests including JSON roundtrips, cycle detection, `bind` composing with `forEach`, radix conversion, toFixed rounding, and Number statics.
- **Built-in library, slice 6d — `Date` (read-only subset)** ✅ — `JsDate : JsObject` with a `Time` slot storing milliseconds since Unix epoch (NaN for an invalid date). `Date()` constructor with three forms: no args (current time), numeric ms, component form (year, month, day?, hours?, minutes?, seconds?, ms?). `Date.now()` static. Read-only prototype methods: `getTime`/`valueOf`, local `getFullYear`/`getMonth`/`getDate`/`getDay`/`getHours`/`getMinutes`/`getSeconds`/`getMilliseconds`, UTC variants of all getters, `getTimezoneOffset`, `toISOString` (spec-exact format), `toJSON` (delegates to `toISOString`), `toString` (browser-style). `JsValue.ToNumber` and `ToJsString` special-case `JsDate` so `b - a` yields the ms difference and `'' + date` emits the ISO string. `JSON.stringify` special-cases `JsDate` so `JSON.stringify(new Date())` emits the ISO string without needing a VM-callback path through `toJSON`. 20 end-to-end tests including component constructor, UTC getters against a fixed epoch ms, date arithmetic, and JSON integration. Deferred: setters, `Date.parse` string parsing, `Date.UTC`, locale methods (`toLocaleString` etc.).
- **Event loop + `console` + timers (slice 7)** ✅ — `JsEngine` now owns a persistent `JsVM` that's reused across every `Evaluate` call and every event-loop task, so scheduled callbacks see bindings established by the main script. `JsVM.RunChunk(chunk)` replaces the old `Run()`, resetting `_ip`/`_sp`/frames/handlers while preserving the globals env. `RunLoop(stopFrameDepth)` was refactored so nested `InvokeJsFunction` calls don't mistakenly honor `_halted` from the outer script. **`JsEventLoop`**: task queue + microtask queue + sorted-by-due-time timer queue with a reverse `_timersById` index for O(log n) `clearTimer`. `Drain()` runs microtasks to completion, then dispatches one task, then checks timers (sleeping until the nearest due time when idle), and repeats until everything is empty or a 100,000-iteration safety limit trips. **`BuiltinConsole`**: `console.log` / `warn` / `error` / `info` / `debug` all append space-separated arguments to a `StringBuilder` on `JsEngine.ConsoleOutput` that hosts read directly. **`BuiltinTimers`**: `setTimeout` / `clearTimeout` / `setInterval` / `clearInterval` / `queueMicrotask` — all as native callables that thunk through the event loop. `setTimeout`/`setInterval` accept extra trailing arguments per the WHATWG spec and forward them to the callback. `JsEngine.RunScript` is a new convenience that calls `Evaluate` + `DrainEventLoop` in one step. Uncaught throws from event-loop tasks escape as `JsRuntimeException` via a catch in `DrainEventLoop` that converts the internal `JsThrowSignal`. 21 end-to-end tests covering console output, `queueMicrotask` FIFO, microtask-schedules-microtask, `setTimeout` with arg forwarding, `clearTimeout`, nested `setTimeout`, callbacks seeing persistent globals, actual wall-clock delay observance, `setInterval` with `clearInterval` from inside the callback, microtask drain between tasks, uncaught-throw host escape, and caught-inside-callback.
- Built-ins: `Object`, `Function`, `Array`, `String`, `Number`, `Boolean`, `Math`, `Date`, `RegExp`, `Error`, `JSON`, `arguments` (slice 6).
- ECMA regex engine (or BCL fallback — see [DD-01](design-decisions.md#dd-01--regex-engine)).
- Event loop (task queue + microtask queue, HTML spec semantics).
- Wire up `console`, `setTimeout`, `setInterval` as the first Web APIs backed by JS.
- Strict mode enforcement.

**Ship gate:** test262 ES5 subset >80% pass.

### Phase 3b — ES2015 core (in progress)

- **`let`/`const` with temporal dead zone + block scoping (slice 3b-1)** ✅ — new `JsUninitialized` sentinel and `DeclareLet` opcode; `BlockStatement` now pushes a fresh env, pre-scans the block for let/const and function declarations, and pops the env on exit. `LoadGlobal` / `LoadGlobalOrUndefined` both check for the sentinel and throw `ReferenceError` so `typeof` of a TDZ binding also throws per spec. For-loop `let` init wraps the whole loop in an env. Function declarations at block scope now hoist into the block env (not the enclosing function), so inner closures capture the block env and can read `let` bindings declared alongside them. Top-level `let` / `const` persist in the globals env across successive `Evaluate` calls (pragmatic REPL-friendly deviation from the spec's module record). Per-iteration freshness for `for (let i ...)` is deferred. 21 end-to-end tests.
- **Arrow functions (slice 3b-2)** ✅ — `ArrowFunctionExpression` AST node. Parser's `ParseAssignmentExpression` peeks for `Identifier =>` and `(...) => body` via a read-only forward scan for the matching close paren; falls through to the normal expression parse when nothing follows the `)`. `JsFunctionTemplate.IsArrow` and `JsFunction.CapturedThis`: the VM snapshots the current `_this` at `MakeFunction` time and the call path uses it instead of the caller's thisVal. Arrow call setup skips binding a fresh `arguments` object, so references resolve up the env chain to the enclosing function's. `DoNew` rejects arrow targets with a `TypeError`. Concise body `x => expr` is lowered to `{ return expr; }` via a synthetic `ReturnStatement` wrapper so it reuses the normal function-body compile path. 21 end-to-end tests including currying, method-with-inner-arrow `this` preservation, `arguments` inheritance from outer, arrows passed to `forEach`/`map`/`filter`/`reduce`, and `new` rejection.
- **Template literals (slice 3b-3)** ✅ — Four new token kinds (`NoSubstitutionTemplate`, `TemplateHead`, `TemplateMiddle`, `TemplateTail`) and a lexer state machine. The lexer tracks `_braceDepth` for `{`/`}` nesting and a `_templateStack` of the brace depths at each active `${` opening; when a `}` is seen at the top-of-stack's matching depth, the lexer switches back to template-string scan mode instead of emitting a `RightBrace` punctuator. This handles nested interpolations (`` `a${`b${c}d`}e` ``) and object literals inside interpolations (`` `${ {a: 1}.a }` ``). Template-literal escape sequences include the usual `\n`/`\t`/etc. plus `\`` and `\$`, and CRLF line endings normalize to LF. `TemplateLiteral` AST node with quasis (decoded string parts) and expressions. The compiler lowers template literals to a sequence of `PushConst` + `Add` operations that reuse the existing string-concat behavior of the `+` operator. 25 end-to-end tests including multi-line, nested templates, method calls inside interpolations, JSON.stringify round-trips, and arrows-plus-templates composition.
- Classes (including static fields, `super`).
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

**Current status:** phase 3a complete — lexer, parser, bytecode VM, objects, arrays, member access, full function / closure / `this` / `new` / `instanceof` system, ES5 control flow, exception handling, the complete ES5 built-in library (`Array`, `String`, `Object`, `Math`, `Error`, `Number`, `Boolean`, `Function.prototype`, `JSON`, `Date`, plus the globals), and the host-side event loop (`console`, `setTimeout`/`clearTimeout`, `setInterval`/`clearInterval`, `queueMicrotask`) are all shipped.

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
