package dev.vodbox.jarhost.android;

import android.content.SharedPreferences;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CopyOnWriteArrayList;

import javax.xml.parsers.DocumentBuilder;
import javax.xml.parsers.DocumentBuilderFactory;

import org.w3c.dom.Document;
import org.w3c.dom.Element;
import org.w3c.dom.Node;
import org.w3c.dom.NodeList;

/**
 * 文件型 {@link SharedPreferences}（VodBox 自有实现）。
 *
 * 存储格式与 Android 兼容：{@code <map>} 下按类型写 {@code <string>/<int>/<long>/<float>/<boolean>/<set>}，
 * 文件位于 {@code <data>/shared_prefs/<name>.xml}。写入为原子（临时文件 + 改名），
 * 读取对缺失文件返回空表。解析时禁用 DTD/外部实体（本地不可信文件也不放行 XXE）。
 *
 * 行为明确：类型不匹配的 {@code getXxx} 返回默认值（不抛），与 Android 的 ClassCastException 相比更宽容，
 * 但永不由 {@code get} 伪造出「存在」的值——只有真正写过的键才 {@code contains}。
 */
public final class FileSharedPreferences implements SharedPreferences {

    private final File file;
    private final Map<String, Object> values = new LinkedHashMap<>();
    private final List<OnSharedPreferenceChangeListener> listeners = new CopyOnWriteArrayList<>();

    /** @param file 目标 XML 文件；null 表示纯内存（不落盘），供测试使用。 */
    public FileSharedPreferences(File file) {
        this.file = file;
        if (file != null && file.isFile()) load(file);
    }

    @Override
    public synchronized Map<String, ?> getAll() {
        return Collections.unmodifiableMap(new LinkedHashMap<>(values));
    }

    @Override
    public synchronized String getString(String key, String defValue) {
        Object value = values.get(key);
        return value instanceof String ? (String) value : defValue;
    }

    @Override
    @SuppressWarnings("unchecked")
    public synchronized Set<String> getStringSet(String key, Set<String> defValues) {
        Object value = values.get(key);
        return value instanceof Set ? new LinkedHashSet<>((Set<String>) value) : defValues;
    }

    @Override
    public synchronized int getInt(String key, int defValue) {
        Object value = values.get(key);
        return value instanceof Integer ? (Integer) value : defValue;
    }

    @Override
    public synchronized long getLong(String key, long defValue) {
        Object value = values.get(key);
        return value instanceof Long ? (Long) value : defValue;
    }

    @Override
    public synchronized float getFloat(String key, float defValue) {
        Object value = values.get(key);
        return value instanceof Float ? (Float) value : defValue;
    }

    @Override
    public synchronized boolean getBoolean(String key, boolean defValue) {
        Object value = values.get(key);
        return value instanceof Boolean ? (Boolean) value : defValue;
    }

    @Override
    public synchronized boolean contains(String key) {
        return values.containsKey(key);
    }

    @Override
    public Editor edit() {
        return new EditorImpl();
    }

    @Override
    public void registerOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener listener) {
        if (listener != null && !listeners.contains(listener)) listeners.add(listener);
    }

    @Override
    public void unregisterOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener listener) {
        listeners.remove(listener);
    }

    private void notifyChanged(Set<String> keys) {
        if (listeners.isEmpty()) return;
        for (String key : keys) {
            for (OnSharedPreferenceChangeListener listener : listeners) {
                listener.onSharedPreferenceChanged(this, key);
            }
        }
    }

    private synchronized boolean persist() {
        if (file == null) return true;
        try {
            if (file.getParentFile() != null) file.getParentFile().mkdirs();
            byte[] xml = serialize().getBytes(StandardCharsets.UTF_8);
            Path target = file.toPath();
            Path temporary = target.resolveSibling(file.getName() + ".tmp");
            Files.write(temporary, xml);
            try {
                Files.move(temporary, target, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
            } catch (IOException atomicUnsupported) {
                Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING);
            }
            return true;
        } catch (IOException error) {
            System.err.println("jarhost: 写入 SharedPreferences 失败：" + file + "（" + error + "）");
            return false;
        }
    }

    private String serialize() {
        StringBuilder builder = new StringBuilder();
        builder.append("<?xml version='1.0' encoding='utf-8' standalone='yes' ?>\n<map>\n");
        for (Map.Entry<String, Object> entry : values.entrySet()) {
            String name = escape(entry.getKey());
            Object value = entry.getValue();
            if (value instanceof String) {
                builder.append("    <string name=\"").append(name).append("\">").append(escape((String) value)).append("</string>\n");
            } else if (value instanceof Integer) {
                builder.append("    <int name=\"").append(name).append("\" value=\"").append(value).append("\" />\n");
            } else if (value instanceof Long) {
                builder.append("    <long name=\"").append(name).append("\" value=\"").append(value).append("\" />\n");
            } else if (value instanceof Float) {
                builder.append("    <float name=\"").append(name).append("\" value=\"").append(value).append("\" />\n");
            } else if (value instanceof Boolean) {
                builder.append("    <boolean name=\"").append(name).append("\" value=\"").append(value).append("\" />\n");
            } else if (value instanceof Set) {
                builder.append("    <set name=\"").append(name).append("\">\n");
                for (Object item : (Set<?>) value) {
                    builder.append("        <string>").append(escape(String.valueOf(item))).append("</string>\n");
                }
                builder.append("    </set>\n");
            }
        }
        builder.append("</map>\n");
        return builder.toString();
    }

    private void load(File source) {
        try {
            byte[] bytes = Files.readAllBytes(source.toPath());
            DocumentBuilderFactory factory = DocumentBuilderFactory.newInstance();
            // 本地不可信文件：禁用 DTD 与外部实体。
            factory.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true);
            factory.setFeature("http://xml.org/sax/features/external-general-entities", false);
            factory.setFeature("http://xml.org/sax/features/external-parameter-entities", false);
            factory.setExpandEntityReferences(false);
            DocumentBuilder builder = factory.newDocumentBuilder();
            Document document;
            try (InputStream input = new ByteArrayInputStream(bytes)) {
                document = builder.parse(input);
            }
            Element root = document.getDocumentElement();
            if (root == null) return;
            NodeList children = root.getChildNodes();
            for (int i = 0; i < children.getLength(); i++) {
                Node node = children.item(i);
                if (node.getNodeType() != Node.ELEMENT_NODE) continue;
                Element element = (Element) node;
                String name = element.getAttribute("name");
                switch (element.getTagName()) {
                    case "string": values.put(name, element.getTextContent()); break;
                    case "int": values.put(name, Integer.valueOf(element.getAttribute("value"))); break;
                    case "long": values.put(name, Long.valueOf(element.getAttribute("value"))); break;
                    case "float": values.put(name, Float.valueOf(element.getAttribute("value"))); break;
                    case "boolean": values.put(name, Boolean.valueOf(element.getAttribute("value"))); break;
                    case "set": values.put(name, readSet(element)); break;
                    default: break;
                }
            }
        } catch (Exception error) {
            System.err.println("jarhost: 读取 SharedPreferences 失败（按空表处理）：" + source + "（" + error + "）");
        }
    }

    private static Set<String> readSet(Element element) {
        Set<String> set = new LinkedHashSet<>();
        NodeList strings = element.getElementsByTagName("string");
        for (int i = 0; i < strings.getLength(); i++) set.add(strings.item(i).getTextContent());
        return set;
    }

    private static String escape(String value) {
        if (value == null) return "";
        StringBuilder builder = new StringBuilder(value.length() + 16);
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            switch (c) {
                case '&': builder.append("&amp;"); break;
                case '<': builder.append("&lt;"); break;
                case '>': builder.append("&gt;"); break;
                case '"': builder.append("&quot;"); break;
                case '\'': builder.append("&apos;"); break;
                default:
                    if (c < 0x20 && c != '\t' && c != '\n') builder.append(' ');
                    else builder.append(c);
            }
        }
        return builder.toString();
    }

    private final class EditorImpl implements Editor {
        private final Map<String, Object> staged = new LinkedHashMap<>();
        private final Set<String> removed = new LinkedHashSet<>();
        private boolean cleared;

        @Override public Editor putString(String key, String value) { return stage(key, value); }
        @Override public Editor putInt(String key, int value) { return stage(key, Integer.valueOf(value)); }
        @Override public Editor putLong(String key, long value) { return stage(key, Long.valueOf(value)); }
        @Override public Editor putFloat(String key, float value) { return stage(key, Float.valueOf(value)); }
        @Override public Editor putBoolean(String key, boolean value) { return stage(key, Boolean.valueOf(value)); }

        @Override
        public Editor putStringSet(String key, Set<String> values) {
            if (values == null) throw new IllegalArgumentException("values 不能为 null（使用 remove 删除）");
            return stage(key, new LinkedHashSet<>(values));
        }

        @Override
        public Editor remove(String key) {
            removed.add(key);
            staged.remove(key);
            return this;
        }

        @Override
        public Editor clear() {
            cleared = true;
            staged.clear();
            removed.clear();
            return this;
        }

        private Editor stage(String key, Object value) {
            if (key == null) throw new IllegalArgumentException("key 不能为 null");
            if (value == null) throw new IllegalArgumentException("value 不能为 null（使用 remove 删除）");
            removed.remove(key);
            staged.put(key, value);
            return this;
        }

        @Override
        public boolean commit() {
            Set<String> changed = new LinkedHashSet<>();
            synchronized (FileSharedPreferences.this) {
                if (cleared) {
                    changed.addAll(values.keySet());
                    values.clear();
                }
                changed.addAll(removed);
                values.keySet().removeAll(removed);
                staged.keySet().forEach(changed::add);
                values.putAll(staged);
                persist();
            }
            notifyChanged(changed);
            return true;
        }

        @Override
        public void apply() {
            commit();
        }
    }

    /** 不落盘的内存实例（{@code file == null} 时行为一致，仅不持久化）。 */
    public static FileSharedPreferences inMemory() {
        return new FileSharedPreferences(null);
    }
}
