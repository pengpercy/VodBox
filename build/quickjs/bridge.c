/*
 * VodBox QuickJS bridge v2 —— drpy 运行时宿主桥。
 *
 * v1（53 行）只支持同步 __host(op, payload)；drpy2.min.js 是 ES modules +
 * 大量异步宿主函数（request/setTimeout…），v2 扩展：
 *   1. JS_MODULE_LOADER：模块解析回调进 C#（assets:// 虚拟协议 + 随包依赖库）
 *   2. 异步宿主函数：__host_async(op, payload) 返回 Promise，
 *      C# 完成后通过 vb_resolve(vm, requestId, resultJson) 回填
 *   3. 期限从 15s 整体 eval 改为可续期（异步桥期间重置，防长链误杀）
 *
 * 安全边界沿用 v1：64MiB 内存 / 1MiB 栈 / 单调钟中断。
 */
#include "quickjs.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>
#ifdef _WIN32
#include <windows.h>
#define EXPORT __declspec(dllexport)
#else
#include <pthread.h>
#define EXPORT __attribute__((visibility("default")))
#endif

typedef struct VM
{
    JSRuntime *rt;
    JSContext *ctx;
    /* 宿主回调：op + payload JSON → 应答 JSON（同步；快速操作用） */
    char *(*host)(const char *, const char *);
    void (*free_host)(char *);
    /* 模块加载器：模块名 → 源码（UTF-8，C 侧不拥有）；找不到返回 NULL */
    char *(*module_loader)(const char *);
    void (*free_module)(char *);
    /* 异步宿主：op + payload + requestId → 0=受理，非 0=未受理（同步失败） */
    int (*host_async)(const char *, const char *, int request_id);
    double deadline;
    int next_request_id;
} VM;

static double now(void)
{
#ifdef _WIN32
    return (double)GetTickCount64() / 1000.0;
#else
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec + t.tv_nsec / 1e9;
#endif
}

static int interrupt(JSRuntime *rt, void *opaque)
{
    (void)rt;
    return now() > ((VM *)opaque)->deadline;
}

static char *copy_string(const char *s)
{
    size_t n = strlen(s) + 1;
    char *p = malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* __host(op, payload) → string（同步宿主操作） */
static JSValue host_call(JSContext *ctx, JSValueConst self, int argc, JSValueConst *argv)
{
    (void)self;
    VM *vm = JS_GetContextOpaque(ctx);
    if (argc < 2 || !vm->host) return JS_ThrowTypeError(ctx, "Host not initialized");
    const char *op = JS_ToCString(ctx, argv[0]);
    const char *payload = JS_ToCString(ctx, argv[1]);
    if (!op || !payload)
    {
        if (op) JS_FreeCString(ctx, op);
        if (payload) JS_FreeCString(ctx, payload);
        return JS_EXCEPTION;
    }
    char *reply = vm->host(op, payload);
    JS_FreeCString(ctx, op);
    JS_FreeCString(ctx, payload);
    if (!reply) return JS_ThrowInternalError(ctx, "Host operation failed");
    JSValue result = JS_NewString(ctx, reply);
    vm->free_host(reply);
    return result;
}

/* __host_async(op, payload) → Promise<string>
 * 受理后 C# 在完成时调用 vb_resolve(vm, request_id, result, is_error)。
 * 异步期间重置中断期限，长 HTTP 链不被 15s 误杀。 */
static JSValue host_async_call(JSContext *ctx, JSValueConst self, int argc, JSValueConst *argv)
{
    (void)self;
    VM *vm = JS_GetContextOpaque(ctx);
    if (argc < 2 || !vm->host_async) return JS_ThrowTypeError(ctx, "Async host not initialized");
    const char *op = JS_ToCString(ctx, argv[0]);
    const char *payload = JS_ToCString(ctx, argv[1]);
    if (!op || !payload)
    {
        if (op) JS_FreeCString(ctx, op);
        if (payload) JS_FreeCString(ctx, payload);
        return JS_EXCEPTION;
    }
    int request_id = ++vm->next_request_id;
    /* quickjs-ng：resolving_funcs 是 [resolve, reject] 二元素数组，各自带引用（需 Free） */
    JSValue resolving_funcs[2];
    JSValue promise = JS_NewPromiseCapability(ctx, resolving_funcs);
    if (JS_IsException(promise))
    {
        JS_FreeCString(ctx, op);
        JS_FreeCString(ctx, payload);
        return promise;
    }
    /* (resolve, reject) 按 request_id 存全局表，vb_resolve 时取回 */
    JSValue global = JS_GetGlobalObject(ctx);
    JSValue resolves = JS_GetPropertyStr(ctx, global, "__resolves");
    JSValue rejects = JS_GetPropertyStr(ctx, global, "__rejects");
    /* JS_SetPropertyInt64 语义是窃取引用：NewPromiseCapability 出参的两个引用
       被表拿走，此处不得再 FreeValue（否则 double free → 堆损坏/栈溢出）。 */
    JS_SetPropertyInt64(ctx, resolves, request_id, resolving_funcs[0]);
    JS_SetPropertyInt64(ctx, rejects, request_id, resolving_funcs[1]);
    JS_FreeValue(ctx, resolves);
    JS_FreeValue(ctx, rejects);
    JS_FreeValue(ctx, global);

    int accepted = vm->host_async(op, payload, request_id);
    JS_FreeCString(ctx, op);
    JS_FreeCString(ctx, payload);
    if (accepted != 0)
    {
        /* 未受理：直接 reject（从数组取回） */
        JS_ThrowInternalError(ctx, "Async host refused: op rejected");
        return JS_EXCEPTION;
    }
    /* 异步等待期间续期 */
    vm->deadline = now() + 15.0;
    return promise;
}

/* JS 模块加载器（ES import）：normal_name → 源码 */
static JSModuleDef *module_loader(JSContext *ctx, const char *module_name, void *opaque)
{
    VM *vm = opaque;
    if (!vm->module_loader) return NULL;
    char *source = vm->module_loader(module_name);
    if (!source) return NULL; /* C# 侧负责日志 */
    JSValue func_val = JS_Eval(ctx, source, strlen(source), module_name, JS_EVAL_TYPE_MODULE | JS_EVAL_FLAG_COMPILE_ONLY);
    vm->free_module(source);
    if (JS_IsException(func_val))
    {
        JS_FreeValue(ctx, func_val);
        return NULL;
    }
    JSModuleDef *module = JS_VALUE_GET_PTR(func_val);
    JS_FreeValue(ctx, func_val);
    if (JS_SetModuleExport(ctx, module, "*", JS_UNDEFINED) < 0)
    {
        /* noop */
    }
    return module;
}

static JSValue resolve_call(JSContext *ctx, JSValueConst self, int argc, JSValueConst *argv)
{
    (void)self;
    (void)argc;
    VM *vm = JS_GetContextOpaque(ctx);
    int request_id = 0;
    if (JS_ToInt32(ctx, &request_id, argv[0]) != 0) return JS_EXCEPTION;
    const char *result = argc > 1 ? JS_ToCString(ctx, argv[1]) : "";
    /* 内部 JS 函数 __doResolve(id, value) —— 由 vb_resolve 注入调用，见下方 C 导出 */
    JSValue global = JS_GetGlobalObject(ctx);
    JSValue resolves = JS_GetPropertyStr(ctx, global, "__resolves");
    JSValue resolve = JS_GetPropertyInt64(ctx, resolves, request_id);
    int present = !JS_IsUndefined(resolve);
    if (present)
    {
        JSValueConst args[1] = { argv[1] };
        JS_Call(ctx, resolve, JS_UNDEFINED, 1, args);
        JS_SetPropertyInt64(ctx, resolves, request_id, JS_UNDEFINED); /* 清引用 */
    }
    JS_FreeValue(ctx, resolve);
    JS_FreeValue(ctx, resolves);
    JS_FreeValue(ctx, global);
    if (result) JS_FreeCString(ctx, result);
    return JS_UNDEFINED;
}

static const JSCFunctionListEntry js_host_funcs[] = {
    JS_CFUNC_DEF("__host", 2, host_call),
    JS_CFUNC_DEF("__host_async", 2, host_async_call),
};

EXPORT VM *vb_create(void)
{
    VM *vm = calloc(1, sizeof(VM));
    if (!vm) return NULL;
    vm->rt = JS_NewRuntime();
    if (!vm->rt) { free(vm); return NULL; }
    JS_SetMemoryLimit(vm->rt, 64 * 1024 * 1024);
    JS_SetMaxStackSize(vm->rt, 1024 * 1024);
    vm->ctx = JS_NewContext(vm->rt);
    if (!vm->ctx) { JS_FreeRuntime(vm->rt); free(vm); return NULL; }
    JS_SetContextOpaque(vm->ctx, vm);
    JS_SetInterruptHandler(vm->rt, interrupt, vm);
    vm->next_request_id = 0;
    vm->deadline = now() + 15.0;
    JSValue global = JS_GetGlobalObject(vm->ctx);
    JSValue funcs = JS_NewObject(vm->ctx);
    JS_SetPropertyFunctionList(vm->ctx, funcs, js_host_funcs, 2);
    JS_SetPropertyStr(vm->ctx, global, "__hostapi", funcs);
    /* 异步回填表 */
    JSValue resolves = JS_NewObject(vm->ctx);
    JSValue rejects = JS_NewObject(vm->ctx);
    JS_SetPropertyStr(vm->ctx, global, "__resolves", resolves);
    JS_SetPropertyStr(vm->ctx, global, "__rejects", rejects);
    JS_SetPropertyStr(vm->ctx, global, "__doResolve", JS_NewCFunction(vm->ctx, resolve_call, "__doResolve", 2));
    JS_FreeValue(vm->ctx, global);
    /* 模块加载器（ES modules） */
    JS_SetModuleLoaderFunc(vm->rt, NULL, module_loader, vm);
    return vm;
}

EXPORT void vb_set_host(VM *vm, char *(*host)(const char *, const char *), void (*free_host)(char *))
{
    vm->host = host;
    vm->free_host = free_host;
}

EXPORT void vb_set_module_loader(VM *vm, char *(*loader)(const char *), void (*free)(char *))
{
    vm->module_loader = loader;
    vm->free_module = free;
}

EXPORT void vb_set_host_async(VM *vm, int (*host_async)(const char *, const char *, int request_id))
{
    vm->host_async = host_async;
}

/* C# 完成异步宿主操作后回填：is_error != 0 → reject(result) */
EXPORT void vb_resolve(VM *vm, int request_id, const char *result, int is_error)
{
    if (!vm || !vm->ctx) return;
    JSValue global = JS_GetGlobalObject(vm->ctx);
    JSValue table = JS_GetPropertyStr(vm->ctx, global, is_error ? "__rejects" : "__resolves");
    JSValue fn = JS_GetPropertyInt64(vm->ctx, table, request_id);
    if (!JS_IsUndefined(fn) && !JS_IsException(fn))
    {
        JSValue value = JS_NewString(vm->ctx, result ? result : "");
        JSValueConst args[1] = { value };
        JSValue ret = JS_Call(vm->ctx, fn, JS_UNDEFINED, 1, args);
        JS_FreeValue(vm->ctx, ret);
        JS_FreeValue(vm->ctx, value);
    }
    /* 引用语义：Get 持有新引用 → FreeValue 释放它；
       SetPropertyInt64(…, UNDEFINED) 窃取一个新 undefined 引用并释放表内旧 fn。
       两步都保留（各自独立合法），不再重复 Free fn 之外的东西。 */
    JS_SetPropertyInt64(vm->ctx, table, request_id, JS_UNDEFINED);
    JS_FreeValue(vm->ctx, fn);
    JS_FreeValue(vm->ctx, table);
    JS_FreeValue(vm->ctx, global);
}

/* 跑微任务/已完成的 job（Promise 回调）；无返回值，由 C# eval 循环调用 */
EXPORT int vb_pump(VM *vm)
{
    JSContext *ctx;
    for (int i = 0; i < 100; i++)
    {
        int status = JS_ExecutePendingJob(vm->rt, &ctx);
        if (status == 0) return 0; /* 无待处理 job */
        if (status < 0)
        {
            /* job 出错：取异常打到 stderr（异步桥诊断），不让它残留 ctx 毒化后续 eval */
            JSValue exc = JS_GetException(ctx);
            const char *msg = JS_ToCString(ctx, exc);
            if (msg) { fprintf(stderr, "[bridge] async job failed: %s\n", msg); JS_FreeCString(ctx, msg); }
            JS_FreeValue(ctx, exc);
            return -1;
        }
    }
    return 1; /* 还有（防饿死调用方） */
}

EXPORT char *vb_eval(VM *vm, const char *script, int *error)
{
    *error = 0;
    vm->deadline = now() + 15.0;
    JSValue value = JS_Eval(vm->ctx, script, strlen(script), "provider.js", JS_EVAL_TYPE_GLOBAL);
    if (JS_IsException(value))
    {
        *error = 1;
        JS_FreeValue(vm->ctx, value);
        value = JS_GetException(vm->ctx);
    }
    /* 排空 pending jobs（Promise 链） */
    JSContext *job_ctx;
    int status;
    while (!*error && (status = JS_ExecutePendingJob(vm->rt, &job_ctx)) != 0)
    {
        if (status < 0)
        {
            JS_FreeValue(vm->ctx, value);
            value = JS_GetException(job_ctx);
            *error = 1;
            break;
        }
        if (now() > vm->deadline)
        {
            JS_FreeValue(vm->ctx, value);
            value = JS_NewString(vm->ctx, "Execution timeout");
            *error = 1;
            break;
        }
    }
    const char *str = JS_ToCString(vm->ctx, value);
    char *result = copy_string(str ? str : "QuickJS error");
    if (str) JS_FreeCString(vm->ctx, str);
    JS_FreeValue(vm->ctx, value);
    return result;
}

/* eval 模块入口（ES modules 主入口用） */
EXPORT char *vb_eval_module(VM *vm, const char *filename, int *error)
{
    *error = 0;
    vm->deadline = now() + 15.0;
    JSValue value = JS_Eval(vm->ctx, "", 0, filename, JS_EVAL_TYPE_MODULE);
    if (JS_IsException(value))
    {
        *error = 1;
        JS_FreeValue(vm->ctx, value);
        value = JS_GetException(vm->ctx);
    }
    const char *str = JS_ToCString(vm->ctx, value);
    char *result = copy_string(str ? str : "QuickJS error");
    if (str) JS_FreeCString(vm->ctx, str);
    JS_FreeValue(vm->ctx, value);
    return result;
}

EXPORT void vb_free_string(char *value) { free(value); }

EXPORT void vb_destroy(VM *vm)
{
    if (vm)
    {
        JS_FreeContext(vm->ctx);
        JS_FreeRuntime(vm->rt);
        free(vm);
    }
}
