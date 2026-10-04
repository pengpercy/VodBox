#include "quickjs.h"
#include <stdlib.h>
#include <string.h>
#include <time.h>
#ifdef _WIN32
#include <windows.h>
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif
typedef struct { JSRuntime *rt; JSContext *ctx; char *(*host)(const char *, const char *); void (*free_host)(char *); double deadline; } VM;
static double now(void) {
#ifdef _WIN32
    return (double)GetTickCount64() / 1000.0;
#else
    struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (double)t.tv_sec + t.tv_nsec / 1e9;
#endif
}
static int interrupt(JSRuntime *rt, void *opaque) { (void)rt; return now() > ((VM *)opaque)->deadline; }
static char *copy_string(const char *s) { size_t n = strlen(s) + 1; char *p = malloc(n); if (p) memcpy(p, s, n); return p; }
static JSValue host_call(JSContext *ctx, JSValueConst self, int argc, JSValueConst *argv) {
    (void)self; VM *vm = JS_GetContextOpaque(ctx);
    if (argc < 2 || !vm->host) return JS_ThrowTypeError(ctx, "Host not initialized");
    const char *op = JS_ToCString(ctx, argv[0]); const char *payload = JS_ToCString(ctx, argv[1]);
    if (!op || !payload) { if (op) JS_FreeCString(ctx, op); if (payload) JS_FreeCString(ctx, payload); return JS_EXCEPTION; }
    char *reply = vm->host(op, payload); JS_FreeCString(ctx, op); JS_FreeCString(ctx, payload);
    if (!reply) return JS_ThrowInternalError(ctx, "Host operation failed");
    JSValue result = JS_NewString(ctx, reply); vm->free_host(reply); return result;
}
EXPORT VM *vb_create(void) {
    VM *vm = calloc(1, sizeof(VM)); if (!vm) return NULL;
    vm->rt = JS_NewRuntime(); if (!vm->rt) { free(vm); return NULL; }
    JS_SetMemoryLimit(vm->rt, 64 * 1024 * 1024); JS_SetMaxStackSize(vm->rt, 1024 * 1024);
    vm->ctx = JS_NewContext(vm->rt); if (!vm->ctx) { JS_FreeRuntime(vm->rt); free(vm); return NULL; }
    JS_SetContextOpaque(vm->ctx, vm); JS_SetInterruptHandler(vm->rt, interrupt, vm);
    JSValue global = JS_GetGlobalObject(vm->ctx);
    JS_SetPropertyStr(vm->ctx, global, "__host", JS_NewCFunction(vm->ctx, host_call, "__host", 2)); JS_FreeValue(vm->ctx, global); return vm;
}
EXPORT void vb_set_host(VM *vm, char *(*host)(const char *, const char *), void (*free_host)(char *)) { vm->host = host; vm->free_host = free_host; }
EXPORT char *vb_eval(VM *vm, const char *script, int *error) {
    *error = 0; vm->deadline = now() + 15.0;
    JSValue value = JS_Eval(vm->ctx, script, strlen(script), "provider.js", JS_EVAL_TYPE_GLOBAL);
    if (JS_IsException(value)) { *error = 1; JS_FreeValue(vm->ctx, value); value = JS_GetException(vm->ctx); }
    JSContext *job_ctx; int status;
    while (!*error && (status = JS_ExecutePendingJob(vm->rt, &job_ctx)) != 0) {
        if (status < 0) { JS_FreeValue(vm->ctx, value); value = JS_GetException(job_ctx); *error = 1; break; }
        if (now() > vm->deadline) { JS_FreeValue(vm->ctx, value); value = JS_NewString(vm->ctx, "Execution timeout"); *error = 1; break; }
    }
    const char *str = JS_ToCString(vm->ctx, value); char *result = copy_string(str ? str : "QuickJS error");
    if (str) JS_FreeCString(vm->ctx, str); JS_FreeValue(vm->ctx, value); return result;
}
EXPORT void vb_free_string(char *value) { free(value); }
EXPORT void vb_destroy(VM *vm) { if (vm) { JS_FreeContext(vm->ctx); JS_FreeRuntime(vm->rt); free(vm); } }
