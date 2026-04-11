using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsEventLoopTests
{
    // -------- console --------

    [Fact]
    public void Console_log_writes_to_output_buffer()
    {
        var eng = new JsEngine();
        eng.Evaluate("console.log('hello');");
        Assert.Equal("hello\n", eng.ConsoleOutput.ToString());
    }

    [Fact]
    public void Console_log_space_separates_arguments()
    {
        var eng = new JsEngine();
        eng.Evaluate("console.log('a', 'b', 42);");
        Assert.Equal("a b 42\n", eng.ConsoleOutput.ToString());
    }

    [Fact]
    public void Console_log_coerces_values()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            console.log(true);
            console.log([1, 2, 3]);
            console.log(null);
            console.log(undefined);
        ");
        Assert.Equal("true\n1,2,3\nnull\nundefined\n", eng.ConsoleOutput.ToString());
    }

    [Fact]
    public void Console_log_inside_a_loop()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            for (var i = 0; i < 3; i++) console.log('n=' + i);
        ");
        Assert.Equal("n=0\nn=1\nn=2\n", eng.ConsoleOutput.ToString());
    }

    [Fact]
    public void Console_warn_error_info_debug_all_route_to_output()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            console.warn('w');
            console.error('e');
            console.info('i');
            console.debug('d');
        ");
        Assert.Equal("w\ne\ni\nd\n", eng.ConsoleOutput.ToString());
    }

    // -------- queueMicrotask --------

    [Fact]
    public void QueueMicrotask_runs_after_main_script_on_drain()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            queueMicrotask(function () { console.log('micro'); });
            console.log('sync');
        ");
        // Before drain: only synchronous output.
        Assert.Equal("sync\n", eng.ConsoleOutput.ToString());
        eng.DrainEventLoop();
        Assert.Equal("sync\nmicro\n", eng.ConsoleOutput.ToString());
    }

    [Fact]
    public void Multiple_microtasks_run_in_FIFO_order()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            queueMicrotask(function () { console.log('a'); });
            queueMicrotask(function () { console.log('b'); });
            queueMicrotask(function () { console.log('c'); });
        ");
        Assert.Equal("a\nb\nc\n", eng.ConsoleOutput.ToString());
    }

    [Fact]
    public void Microtask_scheduled_from_microtask_still_runs()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            queueMicrotask(function () {
                console.log('first');
                queueMicrotask(function () { console.log('second'); });
            });
        ");
        Assert.Equal("first\nsecond\n", eng.ConsoleOutput.ToString());
    }

    // -------- setTimeout --------

    [Fact]
    public void SetTimeout_with_zero_delay_runs_on_drain()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            setTimeout(function () { console.log('deferred'); }, 0);
            console.log('main');
        ");
        Assert.Equal("main\n", eng.ConsoleOutput.ToString());
        eng.DrainEventLoop();
        Assert.Equal("main\ndeferred\n", eng.ConsoleOutput.ToString());
    }

    [Fact]
    public void SetTimeout_forwards_extra_arguments_to_callback()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            setTimeout(function (a, b) { console.log(a + b); }, 0, 'x', 'y');
        ");
        Assert.Equal("xy\n", eng.ConsoleOutput.ToString());
    }

    [Fact]
    public void SetTimeout_returns_a_numeric_id()
    {
        var eng = new JsEngine();
        eng.Evaluate("var id = setTimeout(function () {}, 0);");
        Assert.IsType<double>(eng.Globals["id"]);
    }

    [Fact]
    public void ClearTimeout_prevents_the_callback_from_running()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            var id = setTimeout(function () { console.log('boom'); }, 0);
            clearTimeout(id);
        ");
        Assert.Equal("", eng.ConsoleOutput.ToString());
    }

    [Fact]
    public void Nested_setTimeout_schedules_a_follow_up()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            setTimeout(function () {
                console.log('first');
                setTimeout(function () { console.log('second'); }, 0);
            }, 0);
        ");
        Assert.Equal("first\nsecond\n", eng.ConsoleOutput.ToString());
    }

    [Fact]
    public void SetTimeout_callback_sees_persistent_globals()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            var message = 'hello';
            setTimeout(function () { console.log(message); }, 0);
        ");
        Assert.Equal("hello\n", eng.ConsoleOutput.ToString());
    }

    [Fact]
    public void SetTimeout_delay_takes_effect()
    {
        var eng = new JsEngine();
        var start = DateTime.UtcNow;
        eng.RunScript("setTimeout(function () { console.log('wake'); }, 50);");
        var elapsed = DateTime.UtcNow - start;
        Assert.Equal("wake\n", eng.ConsoleOutput.ToString());
        Assert.True(elapsed.TotalMilliseconds >= 40,
            $"Expected at least 40 ms delay, got {elapsed.TotalMilliseconds}");
    }

    // -------- setInterval --------

    [Fact]
    public void SetInterval_fires_repeatedly_until_cleared()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            var count = 0;
            var id = setInterval(function () {
                count = count + 1;
                console.log('tick');
                if (count >= 3) clearInterval(id);
            }, 0);
        ");
        Assert.Equal("tick\ntick\ntick\n", eng.ConsoleOutput.ToString());
        Assert.Equal(3.0, eng.Globals["count"]);
    }

    // -------- Microtasks drain before next task --------

    [Fact]
    public void Microtasks_drain_between_tasks()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            setTimeout(function () {
                console.log('task1');
                queueMicrotask(function () { console.log('micro-after-task1'); });
            }, 0);
            setTimeout(function () {
                console.log('task2');
            }, 0);
        ");
        // Per spec: task1 runs, then its microtask, then task2.
        Assert.Equal(
            "task1\nmicro-after-task1\ntask2\n",
            eng.ConsoleOutput.ToString());
    }

    // -------- Exception propagation from a task --------

    [Fact]
    public void Exception_in_a_timer_callback_propagates_to_the_host()
    {
        var eng = new JsEngine();
        var ex = Assert.Throws<JsRuntimeException>(() =>
            eng.RunScript("setTimeout(function () { throw 'boom'; }, 0);"));
        Assert.Equal("boom", ex.JsValue);
    }

    [Fact]
    public void Exception_in_a_timer_callback_can_be_caught_inside_it()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            setTimeout(function () {
                try { throw 'handled'; }
                catch (e) { console.log('caught: ' + e); }
            }, 0);
        ");
        Assert.Equal("caught: handled\n", eng.ConsoleOutput.ToString());
    }

    // -------- RunScript convenience --------

    [Fact]
    public void RunScript_drains_event_loop()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            console.log('sync');
            setTimeout(function () { console.log('async'); }, 0);
        ");
        Assert.Equal("sync\nasync\n", eng.ConsoleOutput.ToString());
    }

    // -------- Integration: the classic "after main script" pattern --------

    [Fact]
    public void Main_script_runs_then_deferred_work_runs()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            var results = [];
            function push(x) { results.push(x); }
            push('start');
            setTimeout(function () { push('timeout'); }, 0);
            queueMicrotask(function () { push('micro'); });
            push('end');
        ");
        var arr = Assert.IsType<JsArray>(eng.Globals["results"]);
        Assert.Collection(
            arr.Elements,
            e => Assert.Equal("start", e),
            e => Assert.Equal("end", e),
            e => Assert.Equal("micro", e),
            e => Assert.Equal("timeout", e));
    }
}
