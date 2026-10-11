package dev.vodbox.jarhost.guard;

import java.lang.reflect.Field;
import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.Objects;

/**
 * 把 guest 原生 guard 需要的 JNI 回调**接入现有反射宿主**（本进程的插件 {@link ClassLoader}）。
 *
 * <p>unidbg 的 {@code Jni} 与 {@code ProxyClassMapper} 都是**接口**，因此本层用
 * {@link Proxy} 动态实现，编译期不需要 unidbg。两个代理的分工：
 * <ul>
 *   <li>{@link #classMapper()}：把 DVM 类名映射到**现有反射宿主**里的真实类（插件 JAR 经
 *       DEX→JVM 或字节码加载后的类，含宿主自带的 {@code android.*}/{@code dalvik.*} 最小子集）；
 *       这使 unidbg 的 {@code ProxyClassFactory} 能把原生 guard 的方法调用反射进真实 Java 对象。</li>
 *   <li>{@link #jni(Object)}：作为 {@code vm.setJni(...)} 的兜底实现。当 unidbg 自身的反射桥
 *       无法处理某次回调时才会走到这里——本层把**能确定语义**的静态/实例字段读写接回宿主，
 *       其余一律抛 {@link UnsupportedOperationException}（由宿主归类为
 *       {@code unsupported_native_library}），**绝不返回伪造的默认值**。</li>
 * </ul>
 *
 * <p>普通 guard 与 FTY（饭太硬）走同一条接线：两者都由 {@code callJNI_OnLoad} 初始化，
 * 之后调用的原生方法入口经同一反射宿主解析；区别仅在诊断信息里的库名。
 */
final class GuardJniHost implements InvocationHandler {
    private final ClassLoader reflectionHost;
    private final Class<?> jniType;
    private final Class<?> mapperType;
    private final Class<?> proxyDvmObjectType;
    private Object proxy;
    private Object mapper;

    GuardJniHost(ClassLoader reflectionHost, Class<?> jniType, Class<?> mapperType, Class<?> proxyDvmObjectType) {
        this.reflectionHost = reflectionHost;
        this.jniType = jniType;
        this.mapperType = mapperType;
        this.proxyDvmObjectType = proxyDvmObjectType;
    }

    /** 供 {@code vm.setJni(...)} 与 {@code ProxyClassFactory.setFallbackJni(...)} 使用的 {@code Jni} 实现。 */
    Object jni() {
        if (proxy == null) proxy = Proxy.newProxyInstance(jniType.getClassLoader(), new Class<?>[]{jniType}, this);
        return proxy;
    }

    /** 供 {@code ProxyClassFactory.configClassNameMapper(...)} 使用的类名映射器。 */
    Object classMapper() {
        if (mapper == null) {
            mapper = Proxy.newProxyInstance(mapperType.getClassLoader(), new Class<?>[]{mapperType},
                    (instance, method, arguments) -> {
                        if (method.getName().equals("map")) return map((String) arguments[0]);
                        return Object.class.cast(defaultObjectMethod(instance, method, arguments));
                    });
        }
        return mapper;
    }

    /** DVM 类名 → 现有反射宿主里的真实类；找不到返回 null（unidbg 会按未实现处理）。 */
    Class<?> map(String className) {
        if (className == null || className.isEmpty()) return null;
        String dotted = className.replace('/', '.');
        try {
            ClassLoader current = NativeMethodBridge.reflectionHost();
            return Class.forName(dotted, false, current == null ? reflectionHost : current);
        } catch (ClassNotFoundException | LinkageError absent) {
            return null;
        }
    }

    @Override
    public Object invoke(Object instance, Method method, Object[] arguments) throws Throwable {
        String name = method.getName();
        switch (name) {
            case "acceptMethod":
            case "acceptField":
                // 放行：真正的解析交给 unidbg 的反射桥（ProxyJni），本层不设白名单。
                return Boolean.TRUE;
            case "toString":
                return "GuardJniHost(" + reflectionHost + ")";
            case "hashCode":
                return System.identityHashCode(instance);
            case "equals":
                return instance == arguments[0];
            default:
                break;
        }
        if (name.startsWith("getStatic") || name.startsWith("get")) {
            if (name.endsWith("Field")) return readField(name, arguments);
        }
        if (name.startsWith("setStatic") || name.startsWith("set")) {
            if (name.endsWith("Field")) return writeField(name, arguments);
        }
        // 方法调用/其它回调不由本层兜底：unidbg 的反射桥（ProxyJni）负责；到这一步说明它也无解。
        throw new UnsupportedOperationException(
                "未接入的 JNI 回调：" + name + " " + signatureOf(arguments)
                        + "（本层只把字段读写接入反射宿主，方法调用由 unidbg 反射桥处理）");
    }

    // ---------- 字段读写（静态 + 实例） ----------

    private Object readField(String callback, Object[] arguments) {
        boolean isStatic = callback.startsWith("getStatic");
        String signature = signatureOf(arguments);
        Field field = resolveField(signature);
        if (field == null) throw unsupported(callback, signature, "反射宿主里找不到该字段");
        Object owner = isStatic ? null : receiverOf(arguments);
        Object value;
        try {
            if (!isStatic) {
                if (owner == null) throw unsupported(callback, signature, "实例字段的目标对象为空");
                field.setAccessible(true);
            }
            value = field.get(owner);
        } catch (IllegalAccessException error) {
            throw unsupported(callback, signature, "字段不可读：" + error);
        }
        return convert(callback, arguments, value);
    }

    private Object writeField(String callback, Object[] arguments) {
        boolean isStatic = callback.startsWith("setStatic");
        String signature = signatureOf(arguments);
        Field field = resolveField(signature);
        if (field == null) throw unsupported(callback, signature, "反射宿主里找不到该字段");
        Object owner = isStatic ? null : receiverOf(arguments);
        Object value = unwrap(arguments[arguments.length - 1]);
        try {
            if (!isStatic) {
                if (owner == null) throw unsupported(callback, signature, "实例字段的目标对象为空");
                field.setAccessible(true);
            }
            field.set(owner, coerce(field.getType(), value));
        } catch (IllegalAccessException error) {
            throw unsupported(callback, signature, "字段不可写：" + error);
        }
        return null;
    }

    /** 解析签名 {@code owner->name:type} 为真实字段；provider 为现有反射宿主。 */
    private Field resolveField(String signature) {
        if (signature == null) return null;
        int arrow = signature.indexOf("->");
        int colon = signature.lastIndexOf(':');
        if (arrow <= 0 || colon <= arrow) return null;
        String owner = signature.substring(0, arrow);
        String name = signature.substring(arrow + 2, colon);
        Class<?> type = map(owner);
        if (type == null) return null;
        try {
            return type.getField(name);
        } catch (NoSuchFieldException absent) {
            return null;
        }
    }

    /** 实例回调的接收者（第 2 个参数）所承载的真实 Java 值。 */
    private Object receiverOf(Object[] arguments) {
        Object receiver = arguments.length > 1 ? arguments[1] : null;
        if (isDvmObject(receiver)) {
            try {
                return Reflect.call(receiver, "getValue");
            } catch (RuntimeException error) {
                return null;
            }
        }
        return null;
    }

    /** 参数是否为 unidbg 的 {@code DvmObject}（按类层次名判定，避免编译期依赖）。 */
    private static boolean isDvmObject(Object argument) {
        if (argument == null) return false;
        for (Class<?> type = argument.getClass(); type != null; type = type.getSuperclass()) {
            if ("com.github.unidbg.linux.android.dvm.DvmObject".equals(type.getName())) return true;
        }
        return false;
    }

    /** 依据回调名把字段值转成 unidbg 期望的类型（对象统一走 ProxyDvmObject.createObject）。 */
    private Object convert(String callback, Object[] arguments, Object value) {
        if (callback.endsWith("ObjectField")) {
            Object vm = vmOf(arguments);
            return ProxyDvmObjectBridge.create(proxyDvmObjectType, vm, value);
        }
        return value;
    }

    /** 若参数是 unidbg 的 DVM 对象，取其承载的真实 Java 值；否则原样返回。 */
    private static Object unwrap(Object argument) {
        if (!isDvmObject(argument)) return argument;
        try {
            return Reflect.call(argument, "getValue");
        } catch (RuntimeException ignored) {
            return argument;
        }
    }

    private static Object coerce(Class<?> type, Object value) {
        if (value == null || type.isInstance(value)) return value;
        if (type == int.class && value instanceof Number number) return number.intValue();
        if (type == long.class && value instanceof Number number) return number.longValue();
        if (type == boolean.class && value instanceof Boolean bool) return bool;
        if (type == float.class && value instanceof Number number) return number.floatValue();
        if (type == double.class && value instanceof Number number) return number.doubleValue();
        if (type == byte.class && value instanceof Number number) return number.byteValue();
        if (type == short.class && value instanceof Number number) return number.shortValue();
        if (type == char.class && value instanceof Number number) return (char) number.intValue();
        return value;
    }

    // ---------- 诊断 ----------

    private static Object vmOf(Object[] arguments) {
        return arguments.length > 0 ? arguments[0] : null;
    }

    /**
     * 还原 DVM 签名 {@code owner->name:type}。
     *
     * <p>JNI 接口对同一回调有 String 与对象两种重载：String 重载直接可用；对象重载里
     * {@code DvmField.getSignature()} 是<b>包私有</b>（无法反射调用），因此用其公开的
     * {@code getFieldName()}/{@code getFieldType()} 加所属类的 {@code getClassName()} 拼回。
     */
    private static String signatureOf(Object[] arguments) {
        for (int i = arguments.length - 1; i >= 0; i--) {
            if (arguments[i] instanceof String text) return text;
        }
        for (int i = arguments.length - 1; i >= 0; i--) {
            Object argument = arguments[i];
            if (argument == null || zeroArg(argument, "getFieldName") == null) continue;
            String owner = ownerClassName(arguments);
            if (owner == null) continue;
            try {
                return owner + "->" + callZeroArg(argument, "getFieldName") + ":" + callZeroArg(argument, "getFieldType");
            } catch (Exception ignored) {
                // 继续尝试其它重载。
            }
        }
        for (int i = arguments.length - 1; i >= 0; i--) {
            Object argument = arguments[i];
            if (argument == null) continue;
            Method signature = zeroArg(argument, "getSignature");
            if (signature == null) continue;
            try {
                return (String) signature.invoke(argument);
            } catch (Exception ignored) {
                // 继续尝试。
            }
        }
        return "?";
    }

    /** 所属 DVM 类名：优先带 {@code getClassName()} 的参数（DvmClass），否则取其 {@code getObjectType()}。 */
    private static String ownerClassName(Object[] arguments) {
        for (Object argument : arguments) {
            if (argument == null) continue;
            Method className = zeroArg(argument, "getClassName");
            if (className == null) continue;
            try {
                return (String) className.invoke(argument);
            } catch (Exception ignored) {
                // 继续。
            }
        }
        for (Object argument : arguments) {
            if (argument == null) continue;
            Method objectType = zeroArg(argument, "getObjectType");
            if (objectType == null) continue;
            try {
                Object type = objectType.invoke(argument);
                Method className = type == null ? null : zeroArg(type, "getClassName");
                if (className != null) return (String) className.invoke(type);
            } catch (Exception ignored) {
                // 继续。
            }
        }
        return null;
    }

    private static Method zeroArg(Object target, String name) {
        for (Method method : target.getClass().getMethods()) {
            if (method.getName().equals(name) && method.getParameterCount() == 0 && !method.isSynthetic()) return method;
        }
        return null;
    }

    private static Object callZeroArg(Object target, String name) throws Exception {
        Method method = zeroArg(target, name);
        if (method == null) return null;
        try {
            return method.invoke(target);
        } catch (java.lang.reflect.InvocationTargetException wrapped) {
            throw wrapped.getCause() instanceof Exception cause ? cause : new IllegalStateException(wrapped.getCause());
        }
    }

    private static UnsupportedOperationException unsupported(String callback, String signature, String detail) {
        return new UnsupportedOperationException("JNI 回调未接入：" + callback + " " + signature + "（" + detail + "）");
    }

    private static Object defaultObjectMethod(Object instance, Method method, Object[] arguments) {
        switch (method.getName()) {
            case "toString":
                return "GuardClassMapper";
            case "hashCode":
                return System.identityHashCode(instance);
            case "equals":
                return instance == arguments[0];
            default:
                return null;
        }
    }

    /** {@code ProxyDvmObject.createObject(VM, Object)} 的反射桥（避免编译期依赖 unidbg）。 */
    static final class ProxyDvmObjectBridge {
        private ProxyDvmObjectBridge() {}

        static Object create(Class<?> proxyDvmObjectType, Object vm, Object value) {
            return Reflect.callStatic(proxyDvmObjectType, "createObject", vm, value);
        }
    }

    @Override
    public boolean equals(Object other) {
        return this == other;
    }

    @Override
    public int hashCode() {
        return Objects.hash(reflectionHost, jniType);
    }
}
