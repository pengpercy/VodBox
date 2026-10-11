package dev.vodbox.jarhost.guard;

import java.lang.reflect.Array;
import java.lang.reflect.Constructor;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.util.Arrays;

/**
 * 对运行时加载的 unidbg API 的**最小反射封装**：编译期不依赖 unidbg，只在运行时按名/按参数解析。
 * 只负责「调用与解包」，不含任何业务判断。
 */
final class Reflect {
    private Reflect() {}

    /** 实例方法调用（按名字 + 参数个数 + 可赋值性解析，支持 varargs 形参）。 */
    static Object call(Object target, String name, Object... arguments) {
        return apply(target, findMethod(target.getClass(), name, arguments), arguments);
    }

    /** 静态方法调用。 */
    static Object callStatic(Class<?> owner, String name, Object... arguments) {
        return apply(null, findMethod(owner, name, arguments), arguments);
    }

    /** 构造实例（支持 varargs 形参，如 {@code AndroidResolver(int, String...)}）。 */
    static Object construct(Class<?> owner, Object... arguments) {
        Constructor<?> constructor = findConstructor(owner, arguments);
        Object[] invocation = invocationArguments(constructor.getParameterTypes(), constructor.isVarArgs(), arguments);
        try {
            return constructor.newInstance(invocation);
        } catch (InvocationTargetException wrapped) {
            throw rethrow(wrapped.getCause());
        } catch (ReflectiveOperationException error) {
            throw new IllegalStateException("无法实例化 " + owner.getName() + "：" + error, error);
        }
    }

    private static Method findMethod(Class<?> owner, String name, Object[] arguments) {
        for (Method method : owner.getMethods()) {
            if (!method.getName().equals(name) || method.isSynthetic()) continue;
            if (fits(method.getParameterTypes(), method.isVarArgs(), arguments)) return method;
        }
        throw new IllegalStateException(
                "unidbg API 不匹配：" + owner.getName() + "." + name + "/" + arguments.length + describe(arguments));
    }

    private static Constructor<?> findConstructor(Class<?> owner, Object[] arguments) {
        for (Constructor<?> constructor : owner.getConstructors()) {
            if (fits(constructor.getParameterTypes(), constructor.isVarArgs(), arguments)) return constructor;
        }
        throw new IllegalStateException(
                "unidbg 构造器不匹配：" + owner.getName() + "/" + arguments.length + describe(arguments));
    }

    private static Object apply(Object target, Method method, Object[] arguments) {
        Object[] invocation = invocationArguments(method.getParameterTypes(), method.isVarArgs(), arguments);
        try {
            return method.invoke(target, invocation);
        } catch (InvocationTargetException wrapped) {
            throw rethrow(wrapped.getCause());
        } catch (ReflectiveOperationException error) {
            throw new IllegalStateException("unidbg API 调用失败：" + method + "：" + error, error);
        }
    }

    /** 形参是否接受给定实参（考虑装箱与 varargs）。 */
    private static boolean fits(Class<?>[] parameters, boolean varArgs, Object[] arguments) {
        if (!varArgs) {
            if (parameters.length != arguments.length) return false;
            for (int i = 0; i < parameters.length; i++) if (!accepts(parameters[i], arguments[i])) return false;
            return true;
        }
        int fixed = parameters.length - 1;
        if (arguments.length < fixed) return false;
        for (int i = 0; i < fixed; i++) if (!accepts(parameters[i], arguments[i])) return false;
        Class<?> component = parameters[fixed].getComponentType();
        for (int i = fixed; i < arguments.length; i++) if (!accepts(component, arguments[i])) return false;
        return true;
    }

    private static boolean accepts(Class<?> parameter, Object argument) {
        if (argument == null) return !parameter.isPrimitive();
        if (parameter.isInstance(argument)) return true;
        return parameter.isPrimitive() && wrapper(parameter).equals(argument.getClass());
    }

    /** 把调用实参打包成 {@code Method.invoke}/{@code Constructor.newInstance} 需要的形式（varargs 收尾数组）。 */
    private static Object[] invocationArguments(Class<?>[] parameters, boolean varArgs, Object[] arguments) {
        if (!varArgs) return arguments;
        int fixed = parameters.length - 1;
        Class<?> component = parameters[fixed].getComponentType();
        // 调用方已显式传入收尾数组（个数组 == 形参个数且末位就是该数组）时原样使用。
        if (arguments.length == parameters.length && parameters[fixed].isInstance(arguments[fixed])) return arguments;
        Object tail = Array.newInstance(component, arguments.length - fixed);
        for (int i = fixed; i < arguments.length; i++) Array.set(tail, i - fixed, arguments[i]);
        Object[] invocation = Arrays.copyOf(arguments, parameters.length);
        invocation[fixed] = tail;
        return invocation;
    }

    private static RuntimeException rethrow(Throwable cause) {
        if (cause instanceof RuntimeException runtime) return runtime;
        if (cause instanceof Error error) throw error;
        return new IllegalStateException(String.valueOf(cause), cause);
    }

    private static Class<?> wrapper(Class<?> primitive) {
        if (primitive == boolean.class) return Boolean.class;
        if (primitive == byte.class) return Byte.class;
        if (primitive == char.class) return Character.class;
        if (primitive == short.class) return Short.class;
        if (primitive == int.class) return Integer.class;
        if (primitive == long.class) return Long.class;
        if (primitive == float.class) return Float.class;
        if (primitive == double.class) return Double.class;
        return primitive;
    }

    private static String describe(Object[] arguments) {
        StringBuilder builder = new StringBuilder("(");
        for (int i = 0; i < arguments.length; i++) {
            if (i > 0) builder.append(", ");
            builder.append(arguments[i] == null ? "null" : arguments[i].getClass().getSimpleName());
        }
        return builder.append(')').toString();
    }
}
