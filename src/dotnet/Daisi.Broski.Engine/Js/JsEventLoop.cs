using System.Collections.Concurrent;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Host-side event loop that drives <c>setTimeout</c>,
/// <c>setInterval</c>, <c>queueMicrotask</c>, and any other
/// asynchronous work scheduled from script. Matches the spec's
/// task/microtask queue model (ECMA §8.4 / HTML §6.1.4) as far
/// as phase 3a needs:
///
/// 1. Run the oldest task.
/// 2. Drain the microtask queue completely.
/// 3. Repeat until there are no more tasks or timers.
///
/// Timers are kept in a sorted structure keyed by their due
/// time (milliseconds since Unix epoch). When <see cref="Drain"/>
/// has no immediate tasks or microtasks, it sleeps until the
/// next timer fires and then enqueues that timer's callback as
/// a task.
///
/// The loop uses a real wall clock (<see cref="DateTimeOffset.UtcNow"/>)
/// and <see cref="Thread.Sleep"/> to wait for timers. Tests that
/// want to avoid wall-clock flakiness should either use
/// <c>setTimeout</c> with a tiny delay (under 100 ms) or drive
/// microtasks / tasks directly via
/// <see cref="QueueTask"/> / <see cref="QueueMicrotask"/>.
/// </summary>
public sealed class JsEventLoop
{
    private readonly JsEngine _engine;
    private readonly Queue<Action> _tasks = new();
    private readonly Queue<Action> _microtasks = new();

    /// <summary>Thread-safe inbox for callbacks posted from
    /// background threads (WebSocket receive loops, future
    /// async I/O). The main loop drains this into the regular
    /// task queue at the start of every iteration so cross-
    /// thread work runs on the engine thread, preserving the
    /// single-threaded execution model the VM assumes.</summary>
    private readonly ConcurrentQueue<Action> _crossThread = new();

    // Sorted by due-time. Each bucket is a list because
    // multiple timers can share the same exact due-time ms.
    private readonly SortedDictionary<long, List<TimerEntry>> _timers = new();

    // Secondary index from timer ID to (bucket-key, entry) so
    // clearTimeout / clearInterval can find an entry in O(log n).
    private readonly Dictionary<int, TimerHandle> _timersById = new();

    private int _nextTimerId = 1;

    private sealed class TimerEntry
    {
        public int Id;
        public JsFunction Callback = null!;
        public object?[] Args = Array.Empty<object?>();
        /// <summary>For setInterval: the repeat interval in ms. Null for setTimeout.</summary>
        public double? IntervalMs;
        public bool Cancelled;
    }

    private readonly struct TimerHandle
    {
        public readonly long DueAt;
        public readonly TimerEntry Entry;
        public TimerHandle(long dueAt, TimerEntry entry)
        {
            DueAt = dueAt;
            Entry = entry;
        }
    }

    public JsEventLoop(JsEngine engine)
    {
        _engine = engine;
    }

    /// <summary>True if no more work is pending.</summary>
    public bool IsIdle =>
        _tasks.Count == 0 && _microtasks.Count == 0 && _timers.Count == 0
        && _crossThread.IsEmpty;

    // -------------------------------------------------------------------
    // Scheduling
    // -------------------------------------------------------------------

    public void QueueTask(Action task) => _tasks.Enqueue(task);

    public void QueueMicrotask(Action task) => _microtasks.Enqueue(task);

    /// <summary>Post an action from a background thread for
    /// execution on the engine thread. Safe to call from any
    /// thread; the action runs the next time
    /// <see cref="Drain"/> processes its inbox. Use this for
    /// async I/O completions (WebSocket receive, future
    /// background work) — the regular <see cref="QueueTask"/>
    /// / <see cref="QueueMicrotask"/> queues are not
    /// thread-safe.</summary>
    public void PostFromBackground(Action task) => _crossThread.Enqueue(task);

    /// <summary>
    /// Schedule a JS function to run after a delay. Returns the
    /// timer ID. If <paramref name="isInterval"/> is true, the
    /// timer re-fires at the same interval until cancelled.
    /// </summary>
    public int ScheduleTimer(
        double delayMs,
        JsFunction callback,
        object?[] args,
        bool isInterval)
    {
        if (delayMs < 0) delayMs = 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long dueAt = now + (long)delayMs;
        int id = _nextTimerId++;
        var entry = new TimerEntry
        {
            Id = id,
            Callback = callback,
            Args = args,
            IntervalMs = isInterval ? (double?)delayMs : null,
        };
        AddTimer(dueAt, entry);
        return id;
    }

    public void ClearTimer(int id)
    {
        if (_timersById.TryGetValue(id, out var handle))
        {
            handle.Entry.Cancelled = true;
            _timersById.Remove(id);
        }
    }

    private void AddTimer(long dueAt, TimerEntry entry)
    {
        if (!_timers.TryGetValue(dueAt, out var list))
        {
            list = new List<TimerEntry>();
            _timers[dueAt] = list;
        }
        list.Add(entry);
        _timersById[entry.Id] = new TimerHandle(dueAt, entry);
    }

    // -------------------------------------------------------------------
    // Draining
    // -------------------------------------------------------------------

    /// <summary>
    /// Run tasks, microtasks, and timers until the loop is idle
    /// or <paramref name="maxIterations"/> task dispatches have
    /// been performed. The latter is a safety net against
    /// pathological interval loops in tests.
    /// </summary>
    /// <param name="maxIterations">Hard cap on dispatches.</param>
    /// <param name="maxWaitMs">Maximum time to <c>Thread.Sleep</c>
    /// waiting for a future timer's due-time. Drain returns when a
    /// timer's wait would exceed this. Default 250 ms is enough to
    /// let any pending fetch / Promise chain settle on a fast
    /// network without hanging the host on long
    /// <c>setInterval</c>s — sites routinely register 5-minute
    /// stats refreshers, and a one-shot scrape has no business
    /// blocking the caller for that long. Pass <see cref="int.MaxValue"/>
    /// to restore the original "block until the loop is truly
    /// idle" behavior (only safe in interactive UIs that have
    /// some other way to interrupt).</param>
    public void Drain(int maxIterations = 100_000, int maxWaitMs = 250)
    {
        int iterations = 0;
        while (iterations++ < maxIterations)
        {
            // Drain cross-thread posts onto the regular task queue
            // first so background work (WebSocket receives) shows
            // up immediately on the engine thread.
            while (_crossThread.TryDequeue(out var crossThreadAction))
            {
                _tasks.Enqueue(crossThreadAction);
            }

            // Microtasks drain completely before the next task.
            RunMicrotasks();

            if (_tasks.Count > 0)
            {
                var task = _tasks.Dequeue();
                task();
                continue;
            }

            // No immediate tasks — check timers.
            if (_timers.Count == 0) return;

            var next = _timers.First();
            long dueAt = next.Key;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long waitMs = dueAt - now;
            if (waitMs > maxWaitMs)
            {
                // Future timers exceed our patience — let the loop
                // exit so the host can extract the post-script DOM.
                // Background intervals (setInterval(stats, 5min) on
                // peerllm.com / many Next.js sites) live in this
                // bucket; an interactive runtime would re-enter the
                // loop later when the timer fires.
                return;
            }
            if (waitMs > 0)
            {
                Thread.Sleep((int)waitMs);
            }

            // Fire every timer at this due-time key.
            _timers.Remove(dueAt);
            foreach (var entry in next.Value)
            {
                if (entry.Cancelled) continue;

                // Deliberately do NOT remove from _timersById
                // here: a clearInterval call inside the
                // callback itself needs to find the entry by
                // id and flip its Cancelled flag. The final
                // cleanup happens inside the task action below
                // once we know whether to re-schedule.
                var captured = entry;
                _tasks.Enqueue(() =>
                {
                    if (captured.Cancelled)
                    {
                        _timersById.Remove(captured.Id);
                        return;
                    }

                    _engine.Vm.InvokeJsFunction(
                        captured.Callback,
                        JsValue.Undefined,
                        captured.Args);

                    if (captured.IntervalMs.HasValue && !captured.Cancelled)
                    {
                        // Re-schedule for the next tick of the
                        // interval. AddTimer refreshes
                        // _timersById, so a subsequent
                        // clearInterval still finds it.
                        long reNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        long reDue = reNow + (long)captured.IntervalMs.Value;
                        AddTimer(reDue, captured);
                    }
                    else
                    {
                        // One-shot completed, or interval was
                        // cancelled during the callback.
                        _timersById.Remove(captured.Id);
                    }
                });
            }
        }
    }

    private void RunMicrotasks()
    {
        while (_microtasks.Count > 0)
        {
            var m = _microtasks.Dequeue();
            m();
        }
    }
}
