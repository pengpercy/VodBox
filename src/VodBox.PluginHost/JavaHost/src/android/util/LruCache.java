package android.util;

import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Android {@code LruCache} 最小子集（VodBox 自有实现，语义与 Android 一致）。
 *
 * 用访问序 {@link LinkedHashMap} 实现 LRU；{@link #sizeOf} 默认按条目计 1；
 * {@link #create} 默认返回 null（不自动创建）；{@link #entryRemoved} 默认空实现。
 * {@link #get} 未命中时调用 {@link #create}；{@link #put} 超出上限时按 LRU 淘汰。
 * 所有访问方法同步，可跨线程使用。
 */
public class LruCache<K, V> {

    private final LinkedHashMap<K, V> map;
    private int size;
    private int maxSize;
    private int hitCount;
    private int missCount;
    private int createCount;
    private int evictionCount;
    private int putCount;

    public LruCache(int maxSize) {
        if (maxSize <= 0) throw new IllegalArgumentException("maxSize <= 0");
        this.maxSize = maxSize;
        this.map = new LinkedHashMap<>(0, 0.75f, true);
    }

    public void resize(int maxSize) {
        if (maxSize <= 0) throw new IllegalArgumentException("maxSize <= 0");
        synchronized (this) {
            this.maxSize = maxSize;
        }
        trimToSize(maxSize);
    }

    public V get(K key) {
        if (key == null) throw new NullPointerException("key == null");
        V mapValue;
        synchronized (this) {
            mapValue = map.get(key);
            if (mapValue != null) {
                hitCount++;
                return mapValue;
            }
            missCount++;
        }
        V created = create(key);
        if (created == null) return null;
        synchronized (this) {
            createCount++;
            mapValue = map.put(key, created);
            if (mapValue != null) size -= safeSizeOf(key, mapValue);
            size += safeSizeOf(key, created);
        }
        if (mapValue != null) entryRemoved(false, key, mapValue, created);
        trimToSize(maxSize);
        return created;
    }

    public V put(K key, V value) {
        if (key == null || value == null) throw new NullPointerException("key == null || value == null");
        V previous;
        synchronized (this) {
            putCount++;
            size += safeSizeOf(key, value);
            previous = map.put(key, value);
            if (previous != null) size -= safeSizeOf(key, previous);
        }
        if (previous != null) entryRemoved(false, key, previous, value);
        trimToSize(maxSize);
        return previous;
    }

    public V remove(K key) {
        if (key == null) throw new NullPointerException("key == null");
        V previous;
        synchronized (this) {
            previous = map.remove(key);
            if (previous != null) size -= safeSizeOf(key, previous);
        }
        if (previous != null) entryRemoved(false, key, previous, null);
        return previous;
    }

    public void evictAll() {
        trimToSize(-1);
    }

    public synchronized int size() { return size; }

    public synchronized int maxSize() { return maxSize; }

    public synchronized int hitCount() { return hitCount; }

    public synchronized int missCount() { return missCount; }

    public synchronized int createCount() { return createCount; }

    public synchronized int putCount() { return putCount; }

    public synchronized int evictionCount() { return evictionCount; }

    public synchronized Map<K, V> snapshot() {
        return new LinkedHashMap<>(map);
    }

    protected int sizeOf(K key, V value) {
        return 1;
    }

    protected V create(K key) {
        return null;
    }

    protected void entryRemoved(boolean evicted, K key, V oldValue, V newValue) {
    }

    private void trimToSize(int maxSize) {
        while (true) {
            K key;
            V value;
            synchronized (this) {
                if (size < 0 || (map.isEmpty() && size != 0)) {
                    throw new IllegalStateException(getClass().getName() + ".sizeOf() 与缓存状态不一致");
                }
                if (size <= maxSize || map.isEmpty()) break;
                Map.Entry<K, V> toEvict = map.entrySet().iterator().next();
                key = toEvict.getKey();
                value = toEvict.getValue();
                map.remove(key);
                size -= safeSizeOf(key, value);
                evictionCount++;
            }
            entryRemoved(true, key, value, null);
        }
    }

    private int safeSizeOf(K key, V value) {
        int result = sizeOf(key, value);
        if (result < 0) throw new IllegalStateException("sizeOf() 返回负数：" + result);
        return result;
    }

    @Override
    public synchronized String toString() {
        return "LruCache[maxSize=" + maxSize + ",hits=" + hitCount + ",misses=" + missCount + ",size=" + size + "]";
    }
}
