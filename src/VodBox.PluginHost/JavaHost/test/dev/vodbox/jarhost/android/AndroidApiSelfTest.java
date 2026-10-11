package dev.vodbox.jarhost.android;

import android.app.ActivityThread;
import android.app.Application;
import android.content.ContentValues;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.content.res.AssetManager;
import android.database.Cursor;
import android.database.DatabaseUtils;
import android.database.MatrixCursor;
import android.database.sqlite.SQLiteDatabase;
import android.database.sqlite.SQLiteException;
import android.database.sqlite.SQLiteOpenHelper;
import android.graphics.Color;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.CancellationSignal;
import android.os.Environment;
import android.os.Handler;
import android.os.Looper;
import android.os.Message;
import android.os.Process;
import android.os.StatFs;
import android.os.SystemClock;
import android.preference.PreferenceManager;
import android.text.TextUtils;
import android.util.AtomicFile;
import android.util.Base64;
import android.util.Log;
import android.util.LruCache;
import dalvik.system.DexClassLoader;
import dalvik.system.VMRuntime;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.PrintStream;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.TimeUnit;

/**
 * Android API 最小子集的**离线行为自测**（VodBox 自有实现）。
 *
 * 只依赖 JDK，不触网、不启动真实 Android。以 {@code main} 运行，逐项断言真实行为；
 * 任何失败打印明细并以非 0 退出。用法：
 * <pre>
 *   androidapitest.sh
 * </pre>
 * 或手工：
 * <pre>
 *   java -cp &lt;classes&gt; dev.vodbox.jarhost.android.AndroidApiSelfTest [&lt;classesDir&gt;]
 * </pre>
 * {@code classesDir}（可选）是编译输出目录，用于验证 {@link DexClassLoader} 的目录加载路径。
 */
public final class AndroidApiSelfTest {

    private static int passed;
    private static final List<String> failures = new ArrayList<>();

    public static void main(String[] args) throws Exception {
        Path root = Files.createTempDirectory("vodbox-android-selftest");
        String classesDir = args.length > 0 ? args[0] : null;
        try {
            contextAndApplication(root);
            sharedPreferences(root);
            assetManager(root);
            base64();
            log();
            lruCache();
            atomicFile(root);
            uri();
            buildEnvironmentClock(root);
            handlerAndLooper();
            vmRuntime();
            dexClassLoader(root, classesDir);
            packageManager(root);
            textUtils();
            contentValuesAndCursor();
            colorSignatureAndProcess();
            statFsPreferencesAndActivityThread(root);
            sqliteDatabase(root);
            pluginInitFixture(root);
            unsupportedBoundaryIsIntact();
        } finally {
            deleteRecursively(root.toFile());
        }
        System.out.println("AndroidApiSelfTest: " + passed + " 项通过, " + failures.size() + " 项失败");
        for (String failure : failures) System.out.println("  FAIL " + failure);
        if (!failures.isEmpty()) System.exit(1);
    }

    // ---------- 各项 ----------

    private static void contextAndApplication(Path root) throws Exception {
        Context context = newContext(root, "ctx");
        check("Context.getPackageName", "dev.vodbox.plugin".equals(context.getPackageName()));
        check("Application.getApplicationContext == self",
                context instanceof Application && context.getApplicationContext() == context);
        check("Context.getFilesDir 可写", writeAndRead(new File(context.getFilesDir(), "f.txt")));
        check("Context.getCacheDir 存在", context.getCacheDir().isDirectory());
        check("Context.getExternalFilesDir 存在", context.getExternalFilesDir(null).isDirectory());
        check("Context.getSharedPreferences 可打开", context.getSharedPreferences("p", 0) != null);
        check("Context.getClassLoader 透传", context.getClassLoader() != null);
        check("Context.getMainLooper 非空", context.getMainLooper() != null);
        try {
            context.getSystemService(Context.CONNECTIVITY_SERVICE);
            fail("Context.getSystemService 应抛 UnsupportedOperationException");
        } catch (UnsupportedOperationException expected) {
            check("Context.getSystemService 明确拒绝", true);
        }
    }

    private static void sharedPreferences(Path root) {
        File file = root.resolve("prefs/app.xml").toFile();
        FileSharedPreferences prefs = new FileSharedPreferences(file);
        List<String> changed = new CopyOnWriteArrayList<>();
        prefs.registerOnSharedPreferenceChangeListener((shared, key) -> changed.add(key));
        Set<String> tags = new HashSet<>(Arrays.asList("a", "b"));
        prefs.edit()
                .putString("name", "值<&>\"")
                .putInt("count", 7)
                .putLong("stamp", 1234567890123L)
                .putFloat("ratio", 1.5f)
                .putBoolean("flag", true)
                .putStringSet("tags", tags)
                .commit();
        check("prefs 文件落盘", file.isFile());
        check("prefs 监听器被通知", changed.contains("name") && changed.contains("count"));

        // 重新打开同一文件：读取应完全一致（证明是文件型而非纯内存）。
        FileSharedPreferences reloaded = new FileSharedPreferences(file);
        check("prefs 跨实例持久化 string", "值<&>\"".equals(reloaded.getString("name", null)));
        check("prefs 跨实例持久化 int", reloaded.getInt("count", -1) == 7);
        check("prefs 跨实例持久化 long", reloaded.getLong("stamp", -1) == 1234567890123L);
        check("prefs 跨实例持久化 boolean", reloaded.getBoolean("flag", false));
        check("prefs 跨实例持久化 set", tags.equals(reloaded.getStringSet("tags", null)));
        check("prefs 缺省键返回默认值", reloaded.getString("absent", "d").equals("d"));
        check("prefs contains 仅对已写键为真", reloaded.contains("name") && !reloaded.contains("absent"));

        reloaded.edit().remove("name").commit();
        check("prefs remove", !reloaded.contains("name"));
        reloaded.edit().clear().commit();
        check("prefs clear", reloaded.getAll().isEmpty());
    }

    private static void assetManager(Path root) throws Exception {
        Path assets = root.resolve("assets");
        Files.createDirectories(assets.resolve("js"));
        Files.writeString(assets.resolve("rule.json"), "{\"a\":1}");
        Files.writeString(assets.resolve("js/lib.js"), "var x=1;");
        AssetManager manager = new AssetManager(assets.toFile());
        check("AssetManager.open 读取内容",
                "{\"a\":1}".equals(new String(manager.open("rule.json").readAllBytes(), StandardCharsets.UTF_8)));
        check("AssetManager.list 排序", Arrays.equals(manager.list(""), new String[]{"js", "rule.json"}));
        try {
            manager.open("missing.json");
            fail("open 缺失资源应抛 FileNotFoundException");
        } catch (FileNotFoundException expected) {
            check("AssetManager 缺失资源明确失败", true);
        }
        try {
            manager.open("../escape.txt");
            fail("AssetManager 应拒绝越界路径");
        } catch (FileNotFoundException expected) {
            check("AssetManager 拒绝 .. 越界", true);
        }
    }

    private static void base64() {
        byte[] data = "hello world".getBytes(StandardCharsets.UTF_8);
        check("Base64 往返（NO_WRAP）",
                Arrays.equals(data, Base64.decode(Base64.encodeToString(data, Base64.NO_WRAP), Base64.NO_WRAP)));
        check("Base64 DEFAULT 换行", Base64.encodeToString(new byte[100], Base64.DEFAULT).contains("\n"));
        check("Base64 NO_WRAP 不换行", !Base64.encodeToString(new byte[100], Base64.NO_WRAP).contains("\n"));
        check("Base64 URL_SAFE 字母表",
                !Base64.encodeToString(new byte[]{(byte) 0xFB, (byte) 0xFF, (byte) 0xBF}, Base64.URL_SAFE | Base64.NO_WRAP)
                        .contains("+"));
        check("Base64 NO_PADDING 无 =", !Base64.encodeToString(new byte[]{1, 2}, Base64.NO_WRAP | Base64.NO_PADDING).contains("="));
        check("Base64 解码忽略空白", Arrays.equals(new byte[]{1, 2, 3}, Base64.decode(" AQ\nI\tD ", Base64.DEFAULT)));
        try {
            Base64.decode("*not-base64*", Base64.DEFAULT);
            fail("非法 base64 应抛 IllegalArgumentException");
        } catch (IllegalArgumentException expected) {
            check("Base64 非法输入明确失败", true);
        }
    }

    private static void log() {
        PrintStream original = System.err;
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        try {
            System.setErr(new PrintStream(buffer, true, StandardCharsets.UTF_8));
            Log.d("Tag", "hello");
            Log.e("Tag", "boom");
        } catch (Exception impossible) {
            fail("Log 输出异常：" + impossible);
        } finally {
            System.setErr(original);
        }
        String text = buffer.toString(StandardCharsets.UTF_8);
        check("Log.d 格式", text.contains("D/Tag: hello"));
        check("Log.e 格式", text.contains("E/Tag: boom"));
        check("Log.isLoggable", Log.isLoggable("Tag", Log.DEBUG) && !Log.isLoggable("Tag", 1));
    }

    private static void lruCache() {
        LruCache<String, String> cache = new LruCache<>(2);
        cache.put("a", "1");
        cache.put("b", "2");
        cache.get("a"); // 触碰 a -> 最老变 b
        cache.put("c", "3"); // 淘汰 b
        check("LruCache 淘汰最久未用", cache.get("b") == null && "1".equals(cache.get("a")));
        check("LruCache size", cache.size() == 2);
        List<String> evicted = new ArrayList<>();
        LruCache<String, String> observing = new LruCache<>(1) {
            @Override protected void entryRemoved(boolean e, String key, String oldValue, String newValue) {
                if (e) evicted.add(key);
            }
        };
        observing.put("x", "1");
        observing.put("y", "2");
        check("LruCache entryRemoved(evicted)", evicted.equals(List.of("x")));
    }

    private static void atomicFile(Path root) throws Exception {
        File base = root.resolve("atomic/state.txt").toFile();
        // 正确用法：把流交给 finishWrite（由它负责 close/提交），不要自行 close。
        AtomicFile first = new AtomicFile(base);
        FileOutputStream firstStream = first.startWrite();
        firstStream.write("first".getBytes(StandardCharsets.UTF_8));
        first.finishWrite(firstStream);
        check("AtomicFile 首次提交", "first".equals(new String(first.readFully(), StandardCharsets.UTF_8)));

        AtomicFile second = new AtomicFile(base);
        FileOutputStream secondStream = second.startWrite();
        secondStream.write("second".getBytes(StandardCharsets.UTF_8));
        second.finishWrite(secondStream);
        check("AtomicFile 覆盖提交", "second".equals(new String(second.readFully(), StandardCharsets.UTF_8)));
        check("AtomicFile 无 .new 残留", !new File(base.getPath() + ".new").exists());
        check("AtomicFile exists", second.exists());
        second.delete();
        check("AtomicFile delete", !base.exists());
    }

    private static void uri() {
        Uri uri = Uri.parse("https://user@host.example:8080/a/b/c?x=1&y=2&y=3#frag");
        check("Uri.scheme", "https".equals(uri.getScheme()));
        check("Uri.host", "host.example".equals(uri.getHost()));
        check("Uri.port", uri.getPort() == 8080);
        check("Uri.userInfo", "user".equals(uri.getUserInfo()));
        check("Uri.path", "/a/b/c".equals(uri.getPath()));
        check("Uri.lastPathSegment", "c".equals(uri.getLastPathSegment()));
        check("Uri.pathSegments", uri.getPathSegments().equals(List.of("a", "b", "c")));
        check("Uri.queryParameter", "1".equals(uri.getQueryParameter("x")));
        check("Uri.queryParameters 多值", uri.getQueryParameters("y").equals(List.of("2", "3")));
        check("Uri.queryParameterNames", uri.getQueryParameterNames().equals(new HashSet<>(Arrays.asList("x", "y"))));
        check("Uri.fragment", "frag".equals(uri.getFragment()));
        check("Uri.isHierarchical", uri.isHierarchical() && !uri.isRelative());
        check("Uri.toString 原样", uri.toString().equals("https://user@host.example:8080/a/b/c?x=1&y=2&y=3#frag"));
        Uri built = uri.buildUpon().appendQueryParameter("z", "9").build();
        check("Uri.Builder 追加查询", "9".equals(built.getQueryParameter("z")) && "1".equals(built.getQueryParameter("x")));
        check("Uri.encode/decode 往返", "a b/c".equals(Uri.decode(Uri.encode("a b/c", "/"))));
        check("Uri.decode 不把 + 当空格", "+".equals(Uri.decode("+")));
    }

    private static void buildEnvironmentClock(Path root) {
        check("Build.MANUFACTURER", "VodBox".equals(Build.MANUFACTURER));
        check("Build.VERSION.SDK_INT == 30", Build.VERSION.SDK_INT == 30);
        check("Build.VERSION_CODES.R == 30", Build.VERSION_CODES.R == 30);

        File external = root.resolve("external-root").toFile();
        Environment.configure(root.resolve("data-root").toFile(), external);
        check("Environment 外部存储根", Environment.getExternalStorageDirectory().getAbsolutePath().equals(external.getAbsolutePath()));
        check("Environment 状态 mounted", Environment.MEDIA_MOUNTED.equals(Environment.getExternalStorageState()));
        check("Environment 公共目录可写", writeAndRead(new File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "d.txt")));

        long a = SystemClock.uptimeMillis();
        long b = SystemClock.elapsedRealtime();
        SystemClock.sleep(5);
        long c = SystemClock.uptimeMillis();
        check("SystemClock 单调不减", b >= a && c >= a && SystemClock.elapsedRealtimeNanos() > 0);
    }

    private static void handlerAndLooper() throws Exception {
        Looper main = Looper.getMainLooper();
        check("Looper.getMainLooper 稳定", main != null && main == Looper.getMainLooper());
        check("Looper 主线程为 daemon", main.getThread().isDaemon());

        Handler handler = new Handler(main);
        CountDownLatch posted = new CountDownLatch(1);
        check("Handler.post 返回 true", handler.post(posted::countDown));
        check("Handler 异步执行", posted.await(2, TimeUnit.SECONDS));

        // 延迟顺序：10ms 的应先于 60ms 的执行。
        List<String> order = new CopyOnWriteArrayList<>();
        CountDownLatch two = new CountDownLatch(2);
        handler.postDelayed(() -> { order.add("late"); two.countDown(); }, 60);
        handler.postDelayed(() -> { order.add("early"); two.countDown(); }, 10);
        check("Handler.postDelayed 全部执行", two.await(2, TimeUnit.SECONDS));
        check("Handler 按延迟排序", order.equals(List.of("early", "late")));

        // 消息 + handleMessage
        CountDownLatch message = new CountDownLatch(1);
        Handler subclass = new Handler(main) {
            @Override public void handleMessage(Message msg) {
                if (msg.what == 42 && "obj".equals(msg.obj)) message.countDown();
            }
        };
        subclass.sendMessage(subclass.obtainMessage(42, "obj"));
        check("Handler.handleMessage 收到消息", message.await(2, TimeUnit.SECONDS));

        // Message/Bundle
        Message bundleMessage = Message.obtain(handler, 1);
        Bundle bundle = new Bundle();
        bundle.putString("k", "v");
        bundle.putInt("n", 5);
        bundleMessage.setData(bundle);
        check("Bundle 读写", "v".equals(bundleMessage.getData().getString("k")) && bundleMessage.getData().getInt("n") == 5);

        // 删除回调
        Runnable never = () -> fail("removeCallbacks 未生效");
        handler.postDelayed(never, 200);
        check("Handler.hasMessages 反映待处理回调", handler.hasMessages(0));
        handler.removeCallbacks(never);
        check("Handler.removeCallbacks 移除回调", !handler.hasMessages(0));

        // 无 Looper 的线程创建 Handler() 应明确失败
        final boolean[] threw = {false};
        Thread thread = new Thread(() -> {
            try {
                new Handler();
            } catch (RuntimeException expected) {
                threw[0] = true;
            }
        });
        thread.start();
        thread.join(2000);
        check("Handler() 无 Looper 明确失败", threw[0]);
    }

    private static void vmRuntime() {
        VMRuntime runtime = VMRuntime.getRuntime();
        check("VMRuntime 单例", runtime == VMRuntime.getRuntime());
        check("VMRuntime 默认 targetSdkVersion", runtime.getTargetSdkVersion() == Build.VERSION.SDK_INT);
        runtime.setTargetSdkVersion(28);
        check("VMRuntime.setTargetSdkVersion 记录", runtime.getTargetSdkVersion() == 28);
        runtime.setHiddenApiExemptions(new String[]{"L"});
        check("VMRuntime hiddenApiExemptions 记录但不改变行为",
                Arrays.equals(new String[]{"L"}, runtime.getHiddenApiExemptions()));
        check("VMRuntime.vmLibrary", "libart.so".equals(runtime.vmLibrary()));
    }

    private static void dexClassLoader(Path root, String classesDir) throws Exception {
        if (classesDir != null) {
            DexClassLoader loader = new DexClassLoader(classesDir, null, null, AndroidApiSelfTest.class.getClassLoader());
            Class<?> type = loader.loadClass("android.util.Base64");
            check("DexClassLoader 目录加载 JVM 类", type == Base64.class);
        }
        File notAClass = root.resolve("plain.bin").toFile();
        Files.writeString(notAClass.toPath(), "not a jar");
        try {
            new DexClassLoader(notAClass.getAbsolutePath(), null, null, null);
            fail("DexClassLoader 遇到非类产物应明确拒绝");
        } catch (UnsupportedOperationException expected) {
            check("DexClassLoader 明确拒绝未转换产物", true);
        }
    }

    private static void packageManager(Path root) throws Exception {
        Context context = newContext(root, "pkg");
        PackageManager manager = context.getPackageManager();
        try {
            check("PackageManager.getPackageInfo 自身包", manager.getPackageInfo(context.getPackageName(), 0).versionName != null);
        } catch (PackageManager.NameNotFoundException unexpected) {
            fail("查询自身包不应失败：" + unexpected);
        }
        try {
            manager.getPackageInfo("com.other.app", 0);
            fail("查询其它包应抛 NameNotFoundException");
        } catch (PackageManager.NameNotFoundException expected) {
            check("PackageManager 不假装其它包已安装", true);
        }
    }

    private static void unsupportedBoundaryIsIntact() {
        check("未实现的 UI/WebView 不伪装可用", !isLoadable("android.webkit.WebView"));
        check("未实现的 Activity 不伪装可用", !isLoadable("android.app.Activity"));
        check("未实现的 View 不伪装可用", !isLoadable("android.view.View"));
        check("未实现的 Toast 不伪装可用", !isLoadable("android.widget.Toast"));
        check("未实现的 ContentResolver 不伪装可用", !isLoadable("android.content.ContentResolver"));
        check("已实现的类确实存在", isLoadable("android.content.Context"));
        check("TextUtils 已实现", isLoadable("android.text.TextUtils"));
        check("SQLite 已实现", isLoadable("android.database.sqlite.SQLiteDatabase"));
    }

    // ---------- P2 Android API 对齐（TextUtils / SQLite / 常用值类型） ----------

    private static void textUtils() {
        check("TextUtils.isEmpty(null/空串)", TextUtils.isEmpty(null) && TextUtils.isEmpty("") && !TextUtils.isEmpty(" "));
        check("TextUtils.equals 内容语义", TextUtils.equals("a", new String("a")) && !TextUtils.equals("a", null));
        check("TextUtils.getTrimmedLength", TextUtils.getTrimmedLength("  a b  ") == 3);
        check("TextUtils.join(Object[])", "1,a,null".equals(TextUtils.join(",", new Object[]{1, "a", null})));
        check("TextUtils.join(Iterable)", "a|b".equals(TextUtils.join("|", Arrays.asList("a", "b"))));
        check("TextUtils.split 保留尾空", Arrays.equals(new String[]{"a", "", "b", ""}, TextUtils.split("a,,b,", ",")));
        check("TextUtils.split 空串→空数组", TextUtils.split("", ",").length == 0);
        check("TextUtils.isDigitsOnly", TextUtils.isDigitsOnly("0123") && !TextUtils.isDigitsOnly("12a"));
        check("TextUtils.isGraphic", TextUtils.isGraphic("ab") && !TextUtils.isGraphic("\n"));
        check("TextUtils.indexOf 字符/序列", TextUtils.indexOf("abcabc", 'b') == 1
                && TextUtils.indexOf("abcabc", "bc", 2) == 4);
        check("TextUtils.lastIndexOf", TextUtils.lastIndexOf("abcabc", "bc", 6) == 4);
        check("TextUtils.regionMatches", TextUtils.regionMatches("abc", 1, "bcd", 0, 2));
        check("TextUtils.concat", "ab".equals(TextUtils.concat("a", "b").toString()));
        check("TextUtils.getReverse", "cba".equals(TextUtils.getReverse("abc", 0, 3).toString()));

        TextUtils.SimpleStringSplitter splitter = new TextUtils.SimpleStringSplitter(';');
        splitter.setString("a;bb;;c");
        List<String> tokens = new ArrayList<>();
        for (String token : splitter) tokens.add(token);
        check("SimpleStringSplitter 逐段切分", tokens.equals(Arrays.asList("a", "bb", "", "c")));
    }

    private static void contentValuesAndCursor() {
        ContentValues values = new ContentValues();
        values.put("name", "值");
        values.put("count", 7);
        values.put("ratio", 1.5d);
        values.put("flag", true);
        values.put("blob", new byte[]{1, 2, 3});
        values.putNull("nothing");
        check("ContentValues 覆盖写入", values.size() == 6);
        values.put("count", 8);
        check("ContentValues 同键覆盖", values.getAsInteger("count") == 8);
        check("ContentValues containsKey/getAsLong", values.containsKey("count") && values.getAsLong("count") == 8L);
        check("ContentValues 字符串→数字转换", values.getAsDouble("ratio") == 1.5d);
        check("ContentValues getAsBoolean（非 0 为 true）", values.getAsBoolean("count"));
        check("ContentValues putNull 与 remove 的区别",
                values.containsKey("nothing") && values.get("nothing") == null);
        values.remove("nothing");
        check("ContentValues remove", !values.containsKey("nothing"));

        MatrixCursor cursor = new MatrixCursor(new String[]{"id", "name"}, 2);
        cursor.addRow(new Object[]{1L, "甲"});
        cursor.addRow(new Object[]{2L, null});
        check("MatrixCursor 行数", cursor.getCount() == 2);
        check("MatrixCursor 列名", Arrays.equals(new String[]{"id", "name"}, cursor.getColumnNames()));
        check("MatrixCursor getColumnIndex 去表前缀", cursor.getColumnIndex("t.name") == 1);
        check("MatrixCursor moveToFirst/getLong", cursor.moveToFirst() && cursor.getLong(0) == 1L);
        check("MatrixCursor getString", "甲".equals(cursor.getString(1)));
        check("MatrixCursor moveToNext/isLast", cursor.moveToNext() && cursor.isLast());
        check("MatrixCursor isNull/getType", cursor.isNull(1) && cursor.getType(1) == Cursor.FIELD_TYPE_NULL);
        check("MatrixCursor getType(integer)", cursor.getType(0) == Cursor.FIELD_TYPE_INTEGER);
        check("MatrixCursor moveToLast 之后 isAfterLast 为假", cursor.moveToLast() && !cursor.isAfterLast());
        check("MatrixCursor 越界 moveToPosition", !cursor.moveToPosition(9) && cursor.isAfterLast());
        try {
            cursor.getColumnIndexOrThrow("absent");
            fail("MatrixCursor 缺列应抛 IllegalArgumentException");
        } catch (IllegalArgumentException expected) {
            check("MatrixCursor getColumnIndexOrThrow 明确失败", true);
        }
        cursor.close();
        check("MatrixCursor close/isClosed", cursor.isClosed() && cursor.getCount() == 0);

        Cursor wrapped = new android.database.CursorWrapper(cursor);
        check("CursorWrapper 转发 isClosed/count", wrapped.isClosed() && wrapped.getCount() == 0);
    }

    private static void colorSignatureAndProcess() {
        check("Color.rgb/argb", Color.rgb(1, 2, 3) == 0xFF010203 && Color.argb(0x0A, 1, 2, 3) == 0x0A010203);
        check("Color 通道拆包", Color.alpha(0x0A010203) == 0x0A && Color.red(0x0A010203) == 1
                && Color.green(0x0A010203) == 2 && Color.blue(0x0A010203) == 3);
        check("Color.parseColor #RRGGBB", Color.parseColor("#112233") == 0xFF112233);
        check("Color.parseColor #AARRGGBB", Color.parseColor("#80112233") == 0x80112233);
        check("Color.parseColor 命名色（大小写无关）", Color.parseColor("LightGrey") == Color.LTGRAY);
        try {
            Color.parseColor("#FFF");
            fail("Color.parseColor 应拒绝 Web 简写 #RGB（Android 亦拒绝）");
        } catch (IllegalArgumentException expected) {
            check("Color.parseColor 明确拒绝非法颜色", true);
        }

        Signature signature = new Signature("0AFF");
        check("Signature 十六进制解析", Arrays.equals(new byte[]{0x0A, (byte) 0xFF}, signature.toByteArray())
                && "0aff".equals(signature.toCharsString()));
        check("Signature 相等性", signature.equals(new Signature("0aff")));
        try {
            new Signature("abc");
            fail("奇数长度十六进制应抛 IllegalArgumentException");
        } catch (IllegalArgumentException expected) {
            check("Signature 非法文本明确失败", true);
        }

        check("Process.myPid 与当前进程一致", Process.myPid() == Math.toIntExact(ProcessHandle.current().pid()));
        check("Process.is64Bit 与 JVM 位宽一致",
                Process.is64Bit() == System.getProperty("sun.arch.data.model", "").contains("64"));
        check("Process.myUid 不是 root/系统 uid", Process.myUid() == Process.FIRST_APPLICATION_UID);
        try {
            Process.killProcess(Process.myPid());
            fail("Process.killProcess 应明确拒绝（会断掉协议通道）");
        } catch (UnsupportedOperationException expected) {
            check("Process.killProcess 明确拒绝", true);
        }
    }

    private static void statFsPreferencesAndActivityThread(Path root) throws Exception {
        File directory = root.resolve("statfs").toFile();
        Files.createDirectories(directory.toPath());
        StatFs statFs = new StatFs(directory.getAbsolutePath());
        check("StatFs 真实容量/可用空间", statFs.getTotalBytes() > 0 && statFs.getAvailableBytes() > 0
                && statFs.getAvailableBytes() <= statFs.getTotalBytes());
        check("StatFs 块信息自洽", statFs.getBlockCountLong() * statFs.getBlockSizeLong() <= statFs.getTotalBytes());

        Context context = newContext(root, "prefs-manager");
        SharedPreferences defaults = PreferenceManager.getDefaultSharedPreferences(context);
        check("PreferenceManager 默认文件名与 Android 一致",
                (context.getPackageName() + "_preferences").equals(PreferenceManager.getDefaultSharedPreferencesName(context)));
        defaults.edit().putString("k", "v").commit();
        check("PreferenceManager 默认偏好真实落盘",
                "v".equals(context.getSharedPreferences(context.getPackageName() + "_preferences", 0).getString("k", null))
                        && new File(context.getFilesDir().getParentFile(), "shared_prefs/"
                        + context.getPackageName() + "_preferences.xml").isFile());

        check("ActivityThread.currentApplication 返回真实 Application",
                ActivityThread.currentActivityThread().currentApplication() == context
                        && ActivityThread.currentActivityThread().getApplication() == context);
    }

    /**
     * {@code SQLiteDatabase} 的两条真实路径：**有驱动时**是货真价实的 SQLite（建表/写入/查询/事务/
     * 版本号/冲突），**没有驱动时**必须明确拒绝——不允许静默丢弃写入或把表当空。
     */
    private static void sqliteDatabase(Path root) {
        Path directory = root.resolve("sqlite");
        File file = directory.resolve("probe.db").toFile();
        SQLiteDatabase database = SQLiteDatabase.openOrCreateDatabase(file.getPath(), null);
        check("SQLiteDatabase 打开路径/可写", file.getPath().equals(database.getPath())
                && database.isOpen() && !database.isReadOnly());

        boolean engine = JdbcSqliteEngine.available();
        if (!engine) {
            try {
                database.execSQL("CREATE TABLE t (id INTEGER)");
                fail("缺少 SQLite 驱动时必须明确拒绝，而不是静默丢弃");
            } catch (UnsupportedOperationException expected) {
                check("缺引擎时 execSQL 明确拒绝", expected.getMessage() != null
                        && expected.getMessage().contains("android.database.sqlite.SQLiteDatabase")
                        && expected.getMessage().contains("org.sqlite.JDBC"));
            }
            try {
                database.rawQuery("SELECT 1", null);
                fail("缺少 SQLite 驱动时查询也必须明确拒绝");
            } catch (UnsupportedOperationException expected) {
                check("缺引擎时 rawQuery 明确拒绝", true);
            }
            database.close();
            return;
        }

        System.err.println("AndroidApiSelfTest: SQLite 引擎可用（驱动 " + JdbcSqliteEngine.DRIVER_CLASS + "），执行真实读写断言");
        database.execSQL("CREATE TABLE t (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, payload BLOB)");
        ContentValues values = new ContentValues();
        values.put("name", "甲");
        values.put("payload", new byte[]{1, 2, 3});
        long id = database.insertOrThrow("t", null, values);
        check("insert 返回 rowid", id == 1L);

        ContentValues second = new ContentValues();
        second.put("name", "乙");
        check("insert (非 OrThrow) 返回 rowid", database.insert("t", null, second) == 2L);

        try (Cursor cursor = database.rawQuery("SELECT id, name, payload FROM t ORDER BY id", null)) {
            check("rawQuery 列名", Arrays.equals(new String[]{"id", "name", "payload"}, cursor.getColumnNames()));
            check("rawQuery 行数", cursor.getCount() == 2);
            check("rawQuery moveToFirst", cursor.moveToFirst());
            check("getLong/getType INTEGER", cursor.getLong(0) == 1L && cursor.getType(0) == Cursor.FIELD_TYPE_INTEGER);
            check("getString 中文往返",
                    "甲".equals(cursor.getString(cursor.getColumnIndexOrThrow("name"))));
            check("getBlob 往返", Arrays.equals(new byte[]{1, 2, 3}, cursor.getBlob(2)));
            check("moveToNext 到第二行", cursor.moveToNext() && "乙".equals(cursor.getString(1)));
        }

        try (Cursor built = database.query("t", new String[]{"name"}, "id = ?", new String[]{"2"}, null, null, null)) {
            check("query(table,...) 生成并执行 SQL", built.getCount() == 1 && built.moveToFirst()
                    && "乙".equals(built.getString(0)));
        }
        check("DatabaseUtils.queryNumEntries", DatabaseUtils.queryNumEntries(database, "t") == 2L);

        ContentValues update = new ContentValues();
        update.put("name", "丙");
        check("update 受影响行数", database.update("t", update, "id = ?", new String[]{"2"}) == 1);
        check("delete 受影响行数", database.delete("t", "id = ?", new String[]{"2"}) == 1);

        check("事务回滚不落盘", rollbackLeavesNothing(database));
        check("事务提交落盘", commitPersists(database));

        database.setVersion(3);
        check("setVersion/getVersion 走 PRAGMA user_version", database.getVersion() == 3);
        check("needUpgrade", database.needUpgrade(4) && !database.needUpgrade(3));
        check("compileStatement.simpleQueryForLong",
                database.compileStatement("SELECT COUNT(*) FROM t").simpleQueryForLong() >= 1L);
        check("enableWriteAheadLogging 真实生效", database.enableWriteAheadLogging()
                && database.isWriteAheadLoggingEnabled());
        check("PRAGMA integrity_check", database.isDatabaseIntegrityOk());

        SQLiteDatabase unique = SQLiteDatabase.openOrCreateDatabase(directory.resolve("unique.db").toString(), null);
        try {
            unique.execSQL("CREATE TABLE u (name TEXT UNIQUE)");
            unique.execSQL("INSERT INTO u (name) VALUES ('x')");
            try {
                unique.execSQL("INSERT INTO u (name) VALUES ('x')");
                fail("UNIQUE 冲突应抛 SQLiteConstraintException");
            } catch (android.database.sqlite.SQLiteConstraintException expected) {
                check("UNIQUE 冲突 → SQLiteConstraintException", true);
            }
            ContentValues duplicate = new ContentValues();
            duplicate.put("name", "x");
            check("insert 冲突返回 -1（Android 契约）", unique.insert("u", null, duplicate) == -1L);
            check("insertOrThrow 冲突抛出", insertOrThrowThrows(unique));
        } finally {
            unique.close();
        }

        SQLiteDatabase readOnly = SQLiteDatabase.openDatabase(file.getPath(), null, SQLiteDatabase.OPEN_READONLY);
        check("只读打开 isReadOnly", readOnly.isReadOnly());
        try {
            readOnly.execSQL("DELETE FROM t");
            fail("只读数据库应拒绝写入");
        } catch (SQLiteException expected) {
            check("只读库写入明确失败", true);
        }
        readOnly.close();

        try (Cursor canceled = rawQueryWithCanceledSignal(database)) {
            fail("已取消的 CancellationSignal 应让查询抛出");
        } catch (android.os.OperationCanceledException expected) {
            check("CancellationSignal 已取消时抛 OperationCanceledException", true);
        }

        database.close();
        check("close 后 isOpen 为假", !database.isOpen());
        check("deleteDatabase 删除主文件与边车文件", SQLiteDatabase.deleteDatabase(file) && !file.exists());
    }

    private static boolean rollbackLeavesNothing(SQLiteDatabase database) {
        database.beginTransaction();
        database.execSQL("INSERT INTO t (name) VALUES ('rollback')");
        database.endTransaction();
        return DatabaseUtils.queryNumEntries(database, "t", "name = ?", new String[]{"rollback"}) == 0L;
    }

    private static boolean commitPersists(SQLiteDatabase database) {
        database.beginTransaction();
        database.execSQL("INSERT INTO t (name) VALUES ('commit')");
        database.setTransactionSuccessful();
        database.endTransaction();
        return DatabaseUtils.queryNumEntries(database, "t", "name = ?", new String[]{"commit"}) == 1L;
    }

    private static boolean insertOrThrowThrows(SQLiteDatabase database) {
        ContentValues duplicate = new ContentValues();
        duplicate.put("name", "x");
        try {
            database.insertOrThrow("u", null, duplicate);
            return false;
        } catch (SQLiteException expected) {
            return true;
        }
    }

    private static Cursor rawQueryWithCanceledSignal(SQLiteDatabase database) {
        CancellationSignal signal = new CancellationSignal();
        signal.cancel();
        return database.rawQuery("SELECT * FROM t", null, signal);
    }

    /**
     * **真实验收夹具**：按 {@code Host.java} 的契约反射调用 {@code init(Context, String)}，
     * 再走 home/detail/player 三个方法；SQLite 缓存路径则按引擎是否可用断言「真写」或「明确拒绝」。
     */
    private static void pluginInitFixture(Path root) throws Exception {
        Context context = newContext(root, "fixture");
        FixtureSpider spider = FixtureSpider.class.getDeclaredConstructor().newInstance();
        Method init = FixtureSpider.class.getMethod("init", Context.class, String.class);
        init.invoke(spider, context, "demo-ext");
        check("夹具 init(Context, String) 走通", spider.context() == context);
        check("夹具偏好真实落盘", "demo-ext".equals(spider.preference("extend", "")));

        String home = spider.homeContent(false);
        check("夹具 homeContent 使用 TextUtils 拼接", home.contains("电影,剧集,综艺") && home.contains("首页项"));
        check("夹具 detailContent", spider.detailContent(List.of("100")).contains("\"vod_id\":\"100\""));
        check("夹具 playerContent", spider.playerContent("", "1", List.of()).contains("http://media.invalid/1.m3u8"));

        Method openCache = FixtureSpider.class.getMethod("openCache");
        if (JdbcSqliteEngine.available()) {
            openCache.invoke(spider);
            check("夹具 SQLite 缓存真实写入", spider.cacheCount() == 1);
            check("夹具 SQLiteOpenHelper.onCreate 写入 schema 版本", spider.cacheSchemaVersion() == 1);
        } else {
            try {
                openCache.invoke(spider);
                fail("缺少 SQLite 驱动时夹具缓存必须明确拒绝");
            } catch (InvocationTargetException expected) {
                Throwable cause = expected.getCause();
                check("夹具缓存缺引擎时明确拒绝",
                        cause instanceof UnsupportedOperationException
                                && cause.getMessage() != null
                                && cause.getMessage().contains("android.database.sqlite.SQLiteDatabase"));
            }
        }
    }

    // ---------- 辅助 ----------

    private static Context newContext(Path root, String name) throws Exception {
        Path profile = root.resolve("profile-" + name);
        Files.createDirectories(profile);
        Path external = profile.resolve("external");
        Path assets = profile.resolve("assets");
        Files.createDirectories(assets);
        Environment.configure(profile.resolve("data").toFile(), external.toFile());
        DesktopContext desktop = new DesktopContext(
                AndroidApiSelfTest.class.getClassLoader(), "dev.vodbox.plugin", profile.toFile(),
                external.toFile(), assets.toFile(), "/tmp/plugin.jar", "");
        Application application = new Application();
        application.attachBaseContext(desktop);
        return application;
    }

    private static boolean writeAndRead(File file) {
        try {
            file.getParentFile().mkdirs();
            Files.writeString(file.toPath(), "x");
            return Files.readString(file.toPath()).equals("x");
        } catch (Exception error) {
            return false;
        }
    }

    private static boolean isLoadable(String className) {
        try {
            Class.forName(className, false, AndroidApiSelfTest.class.getClassLoader());
            return true;
        } catch (ClassNotFoundException | LinkageError missing) {
            return false;
        }
    }

    private static void deleteRecursively(File file) {
        File[] children = file.listFiles();
        if (children != null) for (File child : children) deleteRecursively(child);
        file.delete();
    }

    private static void check(String name, boolean condition) {
        if (condition) passed++;
        else fail(name);
    }

    private static void fail(String name) {
        failures.add(name);
    }
}
