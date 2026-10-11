package dev.vodbox.jarhost.android;

import android.database.sqlite.SQLiteConstraintException;
import android.database.sqlite.SQLiteException;

import java.io.File;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.sql.Connection;
import java.sql.Driver;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.ResultSetMetaData;
import java.sql.Statement;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Properties;
import java.util.Set;
import java.util.stream.Stream;

/**
 * {@code android.database.sqlite.*} 的真实 SQLite 引擎（VodBox 自有实现，独立编写）。
 *
 * <h2>为什么是 JDBC</h2>
 * 桌面 JVM **不自带** SQL 引擎（JDK 从 8 起不再附带 JavaDB/Derby），而 Apache-2.0 的 SQLite JDBC
 * 驱动（{@code org.xerial:sqlite-jdbc}）是标准做法。本仓库**不内置、不打包**任何第三方 jar：
 * 驱动与 DEX 转换器（见 {@code dev.vodbox.jarhost.dex.DexToolchain}）一样在**运行时发现**，
 * 本类通过反射加载它，因此 {@code build.sh} 仍然只需要 JDK 标准库。
 *
 * <h2>发现路径（按顺序）</h2>
 * <ol>
 *   <li>宿主 JVM 自身的 classpath（子进程 {@code -cp} 里已放进驱动时命中）；</li>
 *   <li>环境变量 {@code VODBOX_SQLITE_JDBC_CLASSPATH}：路径分隔符分隔的 jar/目录（目录展开为一层 {@code *.jar}）；</li>
 *   <li>环境变量 {@code SQLITE_JDBC_HOME}：解压目录，取其自身与 {@code lib/} 下的 {@code *.jar}；</li>
 *   <li>随包目录：宿主 JAR（本类的 code source）同级的 {@code sqlite-jdbc/}（及其 {@code lib/}），
 *       以及同级 {@code lib/}。</li>
 * </ol>
 *
 * <h2>找不到驱动时的行为（关键）</h2>
 * **不伪装**。需要真正读写数据的操作抛 {@link UnsupportedOperationException}（消息里点明
 * {@code android.database.sqlite.SQLiteDatabase} 与缺失的驱动），由宿主归类为
 * {@code unsupported_android}；<b>绝不</b>把写操作静默丢弃、把查询当成空表，因为那会让插件
 * 以为自己写的缓存/状态真的落盘了。
 *
 * <h2>明确不覆盖</h2>
 * <ul>
 *   <li>只读连接用**软约束**实现（打开可写文件，但拒绝一切写操作并抛与 SQLite 一致的
 *       {@code attempt to write a readonly database}），不用驱动私有的 {@code SQLiteConfig}；</li>
 *   <li>查询结果**物化**为 {@code MatrixCursor}（不保留流式游标）：插件缓存场景下的结果集规模
 *       与本宿主的一次性读取模型一致；</li>
 *   <li>不实现 WAL 调优、`PRAGMA` 之外的驱动高级开关；这些调用是**显式空操作**，不影响可见语义。</li>
 * </ul>
 */
public final class JdbcSqliteEngine implements AutoCloseable {

    /** 显式指定 SQLite JDBC 驱动 classpath（jar 或目录，路径分隔符分隔）。 */
    public static final String CLASSPATH_ENVIRONMENT_VARIABLE = "VODBOX_SQLITE_JDBC_CLASSPATH";
    /** SQLite JDBC 驱动解压目录。 */
    public static final String HOME_ENVIRONMENT_VARIABLE = "SQLITE_JDBC_HOME";
    /** 随包目录名（相对宿主 JAR 所在目录）。 */
    public static final String BUNDLED_DIRECTORY = "sqlite-jdbc";
    /** 本仓库固定使用的驱动实现（xerial sqlite-jdbc，Apache-2.0）。 */
    public static final String DRIVER_CLASS = "org.sqlite.JDBC";

    private static final Object LOCK = new Object();
    private static volatile Driver driver;
    private static volatile String unavailableReason;
    private static volatile boolean probed;

    private final String path;
    private final boolean readOnly;
    private Connection connection;
    private boolean autoCommit = true;
    private boolean closed;

    private JdbcSqliteEngine(String path, boolean readOnly, Connection connection) {
        this.path = path;
        this.readOnly = readOnly;
        this.connection = connection;
    }

    // ---------- 发现与可用性 ----------

    /** 驱动是否可用于真实 SQL 读写；结果在进程内缓存（发现是纯离线动作）。 */
    public static boolean available() {
        return resolveDriver() != null;
    }

    /** 不可用的原因（可用时为 {@code null}）。 */
    public static String unavailableReason() {
        resolveDriver();
        return unavailableReason;
    }

    /** 缺少引擎时的明确拒绝（宿主据此归类为 {@code unsupported_android}）。 */
    public static UnsupportedOperationException unsupported() {
        resolveDriver();
        return new UnsupportedOperationException(
                "android.database.sqlite.SQLiteDatabase 需要 SQLite JDBC 驱动（" + DRIVER_CLASS + "），"
                        + "本宿主不内置、不打包该第三方库：" + (unavailableReason == null ? "未发现" : unavailableReason)
                        + "（可设置 " + CLASSPATH_ENVIRONMENT_VARIABLE + " 或把驱动放到宿主 JAR 同级的 "
                        + BUNDLED_DIRECTORY + "/ 目录）");
    }

    /** 打开（或创建）一个数据库文件；{@code path == null} 或 {@code ":memory:"} 为内存库。 */
    public static JdbcSqliteEngine open(String path, boolean readOnly, boolean createIfNecessary) {
        Driver resolved = resolveDriver();
        if (resolved == null) throw unsupported();
        boolean memory = path == null || path.isEmpty() || ":memory:".equals(path);
        if (!memory) {
            File file = new File(path);
            if (readOnly && !file.isFile()) {
                throw new SQLiteException("unable to open database file (readonly): " + path);
            }
            if (!readOnly && createIfNecessary) {
                File parent = file.getParentFile();
                if (parent != null) parent.mkdirs();
            } else if (!file.isFile()) {
                throw new SQLiteException("unable to open database file: " + path);
            }
        }
        String url = "jdbc:sqlite:" + (memory ? ":memory:" : path);
        Properties properties = new Properties();
        try {
            Connection opened = resolved.connect(url, properties);
            if (opened == null) throw new SQLiteException("driver refused connection: " + url);
            return new JdbcSqliteEngine(path, readOnly, opened);
        } catch (java.sql.SQLException error) {
            throw translate(error);
        }
    }

    /** 驱动实例（一次探测，之后缓存）；不可用时记录原因并返回 {@code null}。 */
    private static Driver resolveDriver() {
        Driver current = driver;
        if (current != null) return current;
        synchronized (LOCK) {
            if (driver != null) return driver;
            if (!probed) {
                probed = true;
                driver = probe();
            }
            return driver;
        }
    }

    private static Driver probe() {
        // 1) 当前 JVM classpath（宿主 JAR / 子进程 -cp）。
        Driver fromSystem = instantiate(JdbcSqliteEngine.class.getClassLoader());
        if (fromSystem != null) return fromSystem;
        // 2) 显式 classpath / 解压目录 / 随包目录。
        Set<Path> jars = new LinkedHashSet<>();
        collectFromPathList(jars, System.getenv(CLASSPATH_ENVIRONMENT_VARIABLE));
        collectFromHome(jars, System.getenv(HOME_ENVIRONMENT_VARIABLE));
        Path home = hostDirectory();
        if (home != null) {
            collectJars(jars, home.resolve(BUNDLED_DIRECTORY));
            collectJars(jars, home.resolve(BUNDLED_DIRECTORY).resolve("lib"));
            collectJars(jars, home.resolve("lib"));
        }
        if (jars.isEmpty()) {
            unavailableReason = "未在 classpath/环境变量/随包目录发现 " + DRIVER_CLASS;
            return null;
        }
        URL[] urls = new URL[jars.size()];
        int index = 0;
        for (Path jar : jars) {
            try {
                urls[index++] = jar.toUri().toURL();
            } catch (java.net.MalformedURLException impossible) {
                index--;
            }
        }
        Driver fromClasspath = instantiate(new URLClassLoader(urls, JdbcSqliteEngine.class.getClassLoader()));
        if (fromClasspath == null) {
            unavailableReason = "发现 " + jars.size() + " 个候选 jar，但没有一个提供 " + DRIVER_CLASS;
        }
        return fromClasspath;
    }

    private static Driver instantiate(ClassLoader loader) {
        try {
            Class<?> type = Class.forName(DRIVER_CLASS, true, loader);
            Object instance = type.getDeclaredConstructor().newInstance();
            return instance instanceof Driver driver ? driver : null;
        } catch (ReflectiveOperationException | LinkageError | RuntimeException error) {
            return null;
        }
    }

    /** 宿主 JAR / classes 目录所在位置；取不到时返回 {@code null}。 */
    private static Path hostDirectory() {
        try {
            URL location = JdbcSqliteEngine.class.getProtectionDomain().getCodeSource().getLocation();
            if (location == null) return null;
            Path resolved = Path.of(location.toURI());
            return Files.isDirectory(resolved) ? resolved : resolved.getParent();
        } catch (Exception error) {
            return null;
        }
    }

    private static void collectFromPathList(Set<Path> target, String value) {
        if (value == null || value.isBlank()) return;
        for (String part : value.split(File.pathSeparator)) {
            String trimmed = part.trim();
            if (trimmed.isEmpty()) continue;
            Path path = Path.of(trimmed);
            if (Files.isDirectory(path)) collectJars(target, path);
            else if (Files.isRegularFile(path)) target.add(path.toAbsolutePath().normalize());
        }
    }

    private static void collectFromHome(Set<Path> target, String home) {
        if (home == null || home.isBlank()) return;
        Path root = Path.of(home.trim());
        if (!Files.isDirectory(root)) return;
        collectJars(target, root);
        collectJars(target, root.resolve("lib"));
    }

    /** 收集目录下一层的 {@code *.jar}（不递归，与 DEX 转换器的发现约定一致）。 */
    private static void collectJars(Set<Path> target, Path directory) {
        if (!Files.isDirectory(directory)) return;
        try (Stream<Path> stream = Files.list(directory)) {
            stream.filter(path -> Files.isRegularFile(path) && path.getFileName().toString().endsWith(".jar"))
                    .map(path -> path.toAbsolutePath().normalize())
                    .sorted()
                    .forEach(target::add);
        } catch (java.io.IOException ignored) {
            // 目录不可读：跳过该来源。
        }
    }

    // ---------- 观察属性 ----------

    public String path() {
        return path;
    }

    public boolean isReadOnly() {
        return readOnly;
    }

    public boolean isOpen() {
        return !closed;
    }

    /** Android 的 {@code SQLiteDatabase.getPath()} 用字符串路径；内存库为 {@code ":memory:"}。 */
    public String describe() {
        return path == null || path.isEmpty() ? ":memory:" : path;
    }

    // ---------- 语句 ----------

    /** {@code execSQL(String)}：执行一条不需要结果集的语句。 */
    public void execute(String sql) {
        execute(sql, null);
    }

    public synchronized void execute(String sql, Object[] arguments) {
        requireWritable();
        try (PreparedStatement statement = prepare(sql, arguments)) {
            statement.execute();
        } catch (java.sql.SQLException error) {
            throw translate(error);
        }
    }

    /** {@code insert/update/delete} 与 {@code execSQL} 共用的「受影响行数」路径。 */
    public synchronized int update(String sql, Object[] arguments) {
        requireWritable();
        try (PreparedStatement statement = prepare(sql, arguments)) {
            return statement.executeUpdate();
        } catch (java.sql.SQLException error) {
            throw translate(error);
        }
    }

    /** {@code insert} 返回新行 rowid（Android 语义：失败返回 -1）。 */
    public synchronized long insert(String sql, Object[] arguments) {
        int affected = update(sql, arguments);
        if (affected <= 0) return -1;
        return scalarLong("SELECT last_insert_rowid()", null);
    }

    /** {@code rawQuery}/{@code query}：物化结果集。 */
    public synchronized android.database.MatrixCursor query(String sql, Object[] arguments) {
        try (PreparedStatement statement = prepare(sql, arguments);
             ResultSet rows = statement.executeQuery()) {
            ResultSetMetaData meta = rows.getMetaData();
            int count = meta.getColumnCount();
            String[] names = new String[count];
            for (int i = 0; i < count; i++) names[i] = meta.getColumnLabel(i + 1);
            android.database.MatrixCursor cursor = new android.database.MatrixCursor(names);
            while (rows.next()) {
                Object[] values = new Object[count];
                for (int i = 0; i < count; i++) values[i] = normalize(rows.getObject(i + 1));
                cursor.addRow(values);
            }
            return cursor;
        } catch (java.sql.SQLException error) {
            throw translate(error);
        }
    }

    public synchronized long scalarLong(String sql, Object[] arguments) {
        String value = scalarString(sql, arguments);
        if (value == null) return 0L;
        try {
            return Long.parseLong(value.trim());
        } catch (NumberFormatException notAnInteger) {
            try {
                return (long) Double.parseDouble(value.trim());
            } catch (NumberFormatException notANumber) {
                throw new SQLiteException("not a number: " + value);
            }
        }
    }

    public synchronized String scalarString(String sql, Object[] arguments) {
        try (PreparedStatement statement = prepare(sql, arguments);
             ResultSet rows = statement.executeQuery()) {
            if (!rows.next()) return null;
            Object value = normalize(rows.getObject(1));
            if (value == null) return null;
            return value instanceof byte[] bytes ? new String(bytes, java.nio.charset.StandardCharsets.UTF_8) : String.valueOf(value);
        } catch (java.sql.SQLException error) {
            throw translate(error);
        }
    }

    // ---------- 事务 ----------

    public synchronized void begin() {
        requireWritable();
        try {
            if (autoCommit) {
                connection.setAutoCommit(false);
                autoCommit = false;
            }
        } catch (java.sql.SQLException error) {
            throw translate(error);
        }
    }

    public synchronized void commit() {
        try {
            if (!autoCommit) connection.commit();
        } catch (java.sql.SQLException error) {
            throw translate(error);
        }
    }

    public synchronized void rollback() {
        try {
            if (!autoCommit) connection.rollback();
        } catch (java.sql.SQLException error) {
            throw translate(error);
        }
    }

    public synchronized void endTransaction() {
        try {
            if (!autoCommit) {
                connection.setAutoCommit(true);
                autoCommit = true;
            }
        } catch (java.sql.SQLException error) {
            throw translate(error);
        }
    }

    // ---------- 版本（Android 用 SQLite 的 user_version） ----------

    public synchronized int getUserVersion() {
        return (int) scalarLong("PRAGMA user_version", null);
    }

    public synchronized void setUserVersion(int version) {
        requireWritable();
        execute("PRAGMA user_version = " + version);
    }

    @Override
    public synchronized void close() {
        if (closed) return;
        closed = true;
        connection = null;
    }

    private boolean hasConnection() {
        return !closed && connection != null;
    }

    private Connection connection() {
        if (!hasConnection()) throw new SQLiteException("database is closed");
        return connection;
    }

    private void requireWritable() {
        if (readOnly) throw new SQLiteException("attempt to write a readonly database");
    }

    private PreparedStatement prepare(String sql, Object[] arguments) throws java.sql.SQLException {
        PreparedStatement statement = connection().prepareStatement(sql);
        if (arguments != null) {
            for (int i = 0; i < arguments.length; i++) bind(statement, i + 1, arguments[i]);
        }
        return statement;
    }

    /**
     * 绑定参数：与 Android/SQLite 的绑定规则一致（null、整数族、浮点族、byte[]、Boolean→0/1、其余 toString）。
     */
    private static void bind(PreparedStatement statement, int index, Object value) throws java.sql.SQLException {
        if (value == null) {
            statement.setNull(index, java.sql.Types.VARCHAR);
        } else if (value instanceof Boolean flag) {
            statement.setLong(index, flag ? 1L : 0L);
        } else if (value instanceof Long || value instanceof Integer || value instanceof Short || value instanceof Byte) {
            statement.setLong(index, ((Number) value).longValue());
        } else if (value instanceof Double || value instanceof Float) {
            statement.setDouble(index, ((Number) value).doubleValue());
        } else if (value instanceof byte[] bytes) {
            statement.setBytes(index, bytes);
        } else {
            statement.setString(index, value.toString());
        }
    }

    /** SQLite 的 INTEGER 统一按 long 暴露（与 Android {@code Cursor.getLong} 一致）。 */
    private static Object normalize(Object value) {
        if (value instanceof Integer || value instanceof Short || value instanceof Byte) {
            return ((Number) value).longValue();
        }
        return value;
    }

    /** 把 JDBC 异常映射为 Android 侧的异常类型（约束冲突单独区分）。 */
    private static SQLiteException translate(java.sql.SQLException error) {
        String message = error.getMessage() == null ? "sqlite failure" : error.getMessage();
        String upper = message.toUpperCase(java.util.Locale.ROOT);
        if (upper.contains("CONSTRAINT")) return new SQLiteConstraintException(message);
        return new SQLiteException(message);
    }
}
