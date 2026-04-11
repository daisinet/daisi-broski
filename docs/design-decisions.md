# Design Decisions

> Long-form write-ups of non-trivial design choices in daisi-broski. Each entry enumerates the options actually considered, the trade-offs, a current recommendation (if any), and the signal that would make us revisit it.
>
> Decisions listed here correspond to the numbered open questions in [architecture.md §11](architecture.md#11-open-questions). Entries are added as questions come up; entries are updated (not rewritten) as we learn more.

---

## Contents

- [DD-01 — Regex engine](#dd-01--regex-engine)
- [DD-05 — JS heap and GC strategy](#dd-05--js-heap-and-gc-strategy)

Numbering matches the architecture.md open-question list; gaps are intentional and will fill in as the other questions get write-ups.

---

## DD-01 — Regex engine

**Status:** open, tentative recommendation below.
**Affects:** `Daisi.Broski.Engine.Js.Builtins.RegExp`, the JS engine's string methods, and any Web API that exposes regex (`URLPattern` eventually).

### What's at stake

JavaScript's `RegExp` is specified by ECMA-262 §22.2. Most patterns real sites ship are simple and behave the same in every engine, but real sites *also* lean on the edge cases: sticky matching (`y`), Unicode flag (`u`), `dotAll` (`s`), Unicode property escapes (`\p{...}`), precise `lastIndex` semantics, backtracking order, and the Unicode definition of `\w`/`\d`/`\b`. Getting these wrong doesn't crash — it silently mismatches, and the user debugs "why does my router regex not work in daisi-broski."

`System.Text.RegularExpressions` is BCL and therefore allowed under our no-third-party rule. But BCL regex is its own dialect (descended from Perl 5/PCRE), not ECMA-262. It over-accepts in some places (balancing groups, `RegexOptions.NonBacktracking`) and under-accepts in others (sticky, `u` surrogate-pair handling, Unicode property escapes pre-.NET 7 — present on .NET 10 but with its own interpretation).

### Options considered

#### A — Plain BCL `Regex`

Just call `new Regex(source, options)`. Translate ECMA flags to `RegexOptions` where possible, pass through otherwise.

- **Pros:** zero code, mature, JIT-compiled matchers, fast.
- **Cons:** BCL dialect ≠ ECMA dialect. Edge cases diverge silently. No exposed per-match step budget, so a hostile `(a+)+$` on a long input can pin a core until the Job Object wall-time limit kicks in (if we set one).
- **Risk profile:** subtle correctness bugs that only surface on specific real sites. Catastrophic-backtracking DoS if the sandbox isn't capping wall time.

#### B — BCL `Regex` with `RegexOptions.ECMAScript` + a source rewriter

`RegexOptions.ECMAScript` exists, but it's frozen at ES3 (1999). It closes some gaps and opens others (no sticky flag, no `u` flag, no `\p{...}`). On top of it we'd run a small transformer over the ECMA source string that:

- Expands `\p{Script=...}` / `\p{General_Category=...}` into explicit character classes, using Unicode tables we ship as a generated C# file.
- Rewrites `.` → `[\s\S]` when the `s` flag is set.
- Implements the sticky `y` flag ourselves by anchoring at `lastIndex` and using a non-ECMAScript outer loop.
- Remaps the `u` flag's surrogate-pair handling by transforming the pattern to explicit surrogate-pair sequences where needed.
- Detects any pattern the rewriter can't handle and falls back to option C for just those.

- **Pros:** keeps most of BCL's speed, closes most of the correctness gap, bounded LOC.
- **Cons:** the rewriter is subtle code with its own bugs. Every new ECMA edition puts us behind. BCL's matcher still owns backtracking behavior, which we can't fully control. Unicode tables add binary size.
- **Risk profile:** medium correctness, OK performance, ongoing maintenance tax.

#### C — Hand-written ECMA-262 regex engine

Full implementation per ECMA-262 §22.2: a parser that builds an IR, a bytecode compiler, a backtracking NFA matcher with a step budget. Reference implementations include Irregexp (V8) and the regex engine in Boa.

- **Pros:** own every edge case. Pass test262's regex suite cleanly. Expose a per-match step budget so catastrophic backtracking becomes a `SyntaxError`-like `InternalError` instead of a CPU hang. Full control over when and how we match.
- **Cons:** significant LOC (estimate 1500–3000 for a credible first version). Almost certainly slower than BCL on common patterns until we add fast paths (literal prefix, Boyer-Moore, DFA for backref-free subsets). Bug surface during the first few months.
- **Risk profile:** high upfront cost, low ongoing cost, correctness by construction.

#### D — Hybrid: BCL for "safe" patterns, custom for the rest

At construction time, parse the ECMA source ourselves. Classify the pattern: if it uses no features we know BCL disagrees about (a whitelist of safe constructs), compile to BCL. Otherwise compile to our own bytecode.

- **Pros:** best average-case performance — common patterns get BCL speed, gnarly patterns get our correctness.
- **Cons:** two regex engines to maintain and test. The classifier is itself a correctness surface (miss a BCL divergence → silent mismatch). We still have to write C anyway.
- **Risk profile:** attractive on paper, tends toward a maintenance tarpit in practice.

#### E — Port Irregexp or Boa algorithmically

Option C, but with the algorithm cribbed from a known-good reference engine instead of derived directly from the spec. Reduces design risk — we're re-implementing something that works — at the cost of being somewhat more constrained.

- **Pros:** known-good design, reasonable performance, fewer first-draft bugs.
- **Cons:** same LOC as C. Not meaningfully different from C once you've read the reference. If we're writing it from scratch anyway, we might as well make it ours.

### Summary table

| Option | Correctness | Perf (typical) | LOC | Maint cost | DoS protection |
|---|---|---|---|---|---|
| A — BCL raw | low | high | 0 | low | none (no step budget) |
| B — BCL + rewriter | medium | medium–high | 300–800 | high | none |
| C — custom NFA | high | medium (improving) | 1500–3000 | medium | built-in |
| D — hybrid | high in theory | high typical | 1500–3000 + classifier | highest | partial |
| E — port of reference | high | medium | 1500–3000 | medium | built-in |

### Tentative recommendation

**Phase 3a–3b: option A** as an unblocking placeholder so the JS engine work can proceed. Most test262 regex failures in the early phases will be swamped by failures elsewhere.

**Phase 3c (before claiming ES2018+ compliance): option C.** Write it ourselves, derive from the spec, bake in a step budget from day one. Do *not* take detour D.

### Revisit when

- test262 regex suite pass rate stalls and the remaining failures are dominated by BCL-dialect divergences.
- A real site breaks in a way that traces back to a sticky/`u`/`\p` issue.
- We measure a catastrophic-backtracking DoS against the sandbox in the wild.
- Binary size of `u`-flag Unicode tables (option B) becomes the blocker, tipping us straight to C.

---

## DD-05 — JS heap and GC strategy

**Status:** open, tentative recommendation below. This one has the biggest structural impact on the engine and is the easiest to get wrong by over-engineering early.
**Affects:** `Daisi.Broski.Engine.Js.Value`, `Heap`, `Scope`, `BytecodeCompiler`, `Interpreter` — essentially every file in the JS engine.

### What's at stake

A JS engine allocates aggressively. A typical SPA's first 500 ms creates hundreds of thousands of transient objects — argument frames, intermediate arithmetic, template string fragments, closure captures, array literals, destructured bindings. How those values are laid out in memory determines:

1. **Working set.** We have a 256 MiB Job Object budget; the CLR eats ~60 MiB cold. JS object representation is the single biggest contributor to what's left.
2. **Latency tail.** The difference between "the .NET GC pauses for 40 ms mid-interpretation" and "we finish a task, then collect" is whether the engine feels responsive.
3. **Compiler and interpreter shape.** A tagged-union value type changes the bytecode's stack representation, which ripples through every opcode.

Getting this right matters. Getting this right *too early*, before we have real profiling data from real sites, is the single biggest time sink on the project.

### Options considered

#### A — Lean on the .NET GC

Every JS object is a plain C# class (`JsObject`, `JsString`, `JsArray`, `JsFunction`, `JsBoundFunction`, ...). Closures capture by field reference. The .NET GC tracks everything; cycles collect automatically; finalization and weak refs come free.

- **Pros:** simplest possible design. Correct by construction. Zero custom memory code. Friendly to the .NET debugger and heap profiler. No class of memory safety bugs we could introduce.
- **Cons:** every JS object pays the C# object header (16 bytes on 64-bit) plus method-table pointer, totalling ~24 bytes before a single field. A JS object with two properties is ~50 bytes here vs ~24 in V8. Primitive values (numbers, booleans, `null`, `undefined`) have to be boxed when stored in a `List<object>`-shaped container, which is most of the VM. Short-lived values thrash .NET's Gen 0 which is tuned for .NET allocation patterns, not JS's.
- **Working-set cost:** high for object-heavy sites (graph libraries, big JSON payloads, React virtual DOM trees).
- **Latency profile:** good average, bad tail — .NET GC pauses when *it* decides to, not when we do.

#### B — Tagged-union `JsValue` struct

`JsValue` is a 16-byte `struct`: 1-byte type tag + 15-byte payload union. Doubles fit inline. Small integers fit inline. Booleans, `null`, `undefined` are just tag values. Small strings (up to ~15 bytes of UTF-8) fit inline. Larger values reference objects on the managed heap by reference. The VM stack, call frames, closure captures, and property storage are all `Span<JsValue>` / `JsValue[]`.

- **Pros:** numbers, booleans, small strings, `null`, and `undefined` never allocate. Stack-heavy computation (arithmetic, control flow, local variable access) is zero-GC. Avoids the header tax on primitives, which are the majority of values in most real code. Still leans on .NET GC for object *identity*, so cycles and finalization remain free.
- **Cons:** `JsValue` is 16 bytes where a bare object reference is 8, so call frames and arrays are 2x wider. Inline-string encoding is fiddly and has to agree in every accessor. Boxing behavior at API boundaries (hand a `JsValue` to the DOM) needs care. Refactoring effort is real — every opcode touches the value representation.
- **Working-set cost:** much better than A for primitive-heavy code, about the same for object-heavy code.
- **Latency profile:** somewhat better than A because less pressure on Gen 0.

#### C — Struct-of-arrays pooled heap

All JS objects live in a fixed set of managed arrays: `JsObject[] _objects`, `JsString[] _strings`, `JsFunction[] _functions`. A "reference" is a 32-bit index + 32-bit type tag packed into 64 bits. When a slot is freed, its index goes on a freelist. We run mark-sweep over these arrays at task boundaries.

- **Pros:** cache-friendly, compact, tight control over generation semantics. We decide *when* GC runs (between tasks, not during user-perceptible work). Serializing the heap for IPC `GetDocument` snapshots is a single array copy. We own the layout end-to-end.
- **Cons:** we have to write GC ourselves — a correct mark-sweep with a work list, cycle handling, weak ref support, finalizer queues. Resizing pools when they outgrow their initial size invalidates every live reference unless we introduce an indirection layer (which costs another cache miss per access). Debugging is painful because the .NET heap profiler doesn't understand our heap. Unsafe code surface is large.
- **Working-set cost:** best of all options for object-heavy sites.
- **Latency profile:** best — deterministic, scheduled between tasks.

#### D — Young-gen arena + old-gen managed (V8-lite)

Two-level heap:

- **Young generation:** a bump-pointer arena over `NativeMemory.AllocZeroed(16 MiB)` (or a pinned managed array). New JS objects allocate by bumping a pointer. At task boundary, or when the arena fills, a mark-copy pass walks the roots; surviving objects get promoted to real C# class instances on the managed heap; the arena resets to empty.
- **Old generation:** managed C# classes. Lives under .NET GC for the long-lived object graph, cycles, and finalization.

This matches how V8's Orinoco, SpiderMonkey's nursery, and JSC's Eden work. JS is extremely bimodal — most values die within a single task — so a dedicated young gen nukes them for free.

- **Pros:** matches how real JS engines do it because it matches how JS actually allocates. Young-gen allocation is a few instructions. Old gen gets .NET GC's maturity for free on the long-lived graph. Latency is predictable: we pay for young-gen collection at task boundaries we choose.
- **Cons:** considerable complexity. Needs escape analysis in the bytecode compiler to decide what can stay in the arena vs. must be promoted immediately (closures escaping, returned objects). Writing a copying young-gen collector correctly is not a weekend project. Promotion has subtle ordering rules (cross-generational references need a write barrier or a scan-old-gen-on-minor-GC strategy). Unsafe code surface is real.
- **Working-set cost:** good, and predictable.
- **Latency profile:** best among options we'd plausibly pick for phase 7 — minor GC is fast because the arena is small, major GC happens only occasionally.

#### E — Region-based with escape analysis

Pure compile-time strategy: the bytecode compiler runs escape analysis and emits explicit `PushRegion` / `PopRegion` opcodes. Values that provably don't outlive their call frame get allocated into a per-frame region and freed on return. Values that might escape fall through to one of the other strategies.

- **Pros:** zero runtime GC overhead for non-escaping values. Deterministic. Composable with A, B, C, or D.
- **Cons:** escape analysis on a dynamic language with `eval`, `with`, dynamic property access, and prototype mutation is hard. A conservative analysis boxes everything that might escape, which is almost everything. You still need a real GC for the rest. This is an *optimization on top of another strategy*, not a strategy by itself.

#### F — Reference counting + cycle collector

`JsValue` carries a reference count. Every assignment and every scope exit does an inc/dec. A separate cycle collector (Python's trial-deletion algorithm or Bacon's synchronous cycle collection) finds unreachable cycles periodically.

- **Pros:** deterministic finalization. Memory freed immediately when count hits zero. Good for debuggability: `this object leaked` is a diagnosable bug.
- **Cons:** every write in the VM is more expensive (count increment, count decrement, conditional free). Multi-threaded refcounting is actively harmful; even single-threaded, the per-assignment cost compounds badly. Modern JS engines considered and rejected this. The cycle collector is itself a nontrivial piece of code. Not a good fit for an engine where arithmetic is on the hot path.

### Summary table

| Option | Simplicity | Working set | Latency | LOC | Bug surface |
|---|---|---|---|---|---|
| A — .NET GC | highest | high | medium avg, bad tail | 0 | near-zero |
| B — tagged union | medium | medium | medium avg, medium tail | +500–1500 refactor | low |
| C — pooled heap | low | lowest | best | +2000–4000 | high |
| D — arena + managed | lowest | low | best | +3000–5000 | highest |
| E — region-based | N/A (add-on) | — | — | compiler changes | medium |
| F — refcounting | medium | medium | bad (write-heavy) | +1500 + cycle collector | medium |

### Tentative recommendation

A three-step progression:

1. **Phase 3a–3b: option A.** Ship correct, get test262 green on the target subset, prove the engine works end-to-end. Do not touch memory layout yet.

2. **Phase 3c: refactor to option B.** Do this *before* `Proxy`/`Reflect` and before the DOM bridge lands, because both of those make the value layer messier and harder to change. Target ~1 week of focused churn across the engine. Big win for primitive-heavy code at moderate cost.

3. **Phase 7 (performance): decide between B alone, D on top, or C replacing B.** By this point we have profiling data from real sites and can identify the actual bottleneck. Possible outcomes:
   - "B is fine, .NET GC keeps up, move on." — most likely.
   - "Minor GC pauses are the latency tail" → add D.
   - "Working set is too high for the 256 MiB Job Object" → migrate to C.

**Options to avoid:**

- **F (refcounting).** A tar pit. Every serious JS engine tried and rejected it.
- **E (region-based) as a primary strategy.** It's a phase-7 add-on to whatever we end up with, not a choice on its own.
- **C or D in phase 3.** Building custom GC before we have profiling data is premature optimization with a huge bug surface. Measure first.

### Revisit when

- Real-site working set exceeds half the Job Object budget and we can trace it to object representation.
- Mid-task GC pauses (not between-task, but during script execution) become user-visible in integration tests.
- A specific site (React 18, Vue 3, something that allocates aggressively) runs 5x+ slower than expected and profiling points at allocation, not interpretation.
- We commit to phase 6 (layout + render) and the combined working-set budget no longer fits under the Job Object cap.
