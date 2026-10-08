using VodBox.PluginHost;

// QuickJS bridge v2 冒烟：同步 eval / console / setTimeout 异步泵 / 模块加载器
Environment.SetEnvironmentVariable("VODBOX_QUICKJS_LIB", "/tmp/qjs-build/libvodbox_quickjs.dylib");

using var engine = new QuickJsEngine();

// 1. 同步 eval + log
Console.WriteLine("== 1. sync eval ==");
Console.WriteLine(engine.Evaluate("1 + 2 * 3")); // 7

// 2. console.log（同步宿主）
Console.WriteLine("== 2. console.log ==");
engine.Evaluate("console.log('hello from QuickJS ' + (typeof globalThis))");

// 3. setTimeout 异步泵
Console.WriteLine("== 3. setTimeout ==");
var r1 = await engine.EvaluateAsync("""
    globalThis.__done = 'no';
    setTimeout(() => { globalThis.__done = 'fired'; }, 30);
    """);
await Task.Delay(80);
var done = engine.Evaluate("globalThis.__done");
Console.WriteLine($"setTimeout fired: {done}");

// 4. Promise + 异步宿主（__host_async setTimeout 链）
Console.WriteLine("== 4. promise chain ==");
await engine.EvaluateAsync("""
    globalThis.__chain = 'pending';
    __hostapi.__host_async('setTimeout', '20').then(v => { globalThis.__chain = 'resolved:' + v; });
    """);
await Task.Delay(60);
Console.WriteLine("chain: " + engine.Evaluate("globalThis.__chain"));

// 5. ES modules
Console.WriteLine("== 5. ES modules ==");
engine.AddModule("mathlib", "export function twice(x) { return x * 2; } export const NAME = 'mathlib';");
var mod = await engine.EvaluateAsync("""
    globalThis.__mod = 'pending';
    import('mathlib').then(m => { globalThis.__mod = m.twice(21) + ':' + m.NAME; });
    """);
await Task.Delay(100);
Console.WriteLine("dynamic import: " + engine.Evaluate("globalThis.__mod"));

// static import（module 顶层）
engine.AddModule("entry", "import { twice } from 'mathlib'; globalThis.__static = twice(50);");
engine.Evaluate("__static='no'");
var ev = await engine.EvaluateAsync("import('entry').then(()=>1).catch(e=>{globalThis.__static='ERR:'+e})");
await Task.Delay(120);
Console.WriteLine("static import: " + engine.Evaluate("globalThis.__static"));

Console.WriteLine("ALL SMOKE DONE");
