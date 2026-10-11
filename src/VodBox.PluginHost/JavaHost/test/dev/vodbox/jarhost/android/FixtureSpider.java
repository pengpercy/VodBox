package dev.vodbox.jarhost.android;

import android.content.ContentValues;
import android.content.Context;
import android.content.SharedPreferences;
import android.database.Cursor;
import android.database.sqlite.SQLiteDatabase;
import android.database.sqlite.SQLiteOpenHelper;
import android.text.TextUtils;
import android.util.Base64;
import android.util.Log;

import java.util.ArrayList;
import java.util.List;

/**
 * **真实插件初始化形状的验收 fixture**（VodBox 自有的测试夹具，不是任何第三方插件代码）。
 *
 * 它模拟普通 CatVod spider 的可观测形状：{@code init(Context, String)} 入口、
 * {@code homeContent}/{@code detailContent}/{@code playerContent} 返回 JSON 字符串、
 * 偏好落盘、可选的 SQLite 缓存。宿主调用它的方式与 {@code Host.java} 一致
 * （反射找 {@code init(Context, String)} 并传入装配好的 Application）。
 *
 * ⚠️ 本夹具只用于离线自测（{@code JavaHost/test.sh}），**不**随宿主打包、不被插件加载。
 * 它的价值是：把「Android 子集的真实行为」放在一个**插件形状**的调用链里验收，
 * 而不是逐个孤立的 API 断言。
 */
public final class FixtureSpider {

    private static final String TAG = "FixtureSpider";

    private Context context;
    private SharedPreferences preferences;
    private Cache cache;
    private String extend = "";
    private String cookie = "";

    public void init(Context context, String extend) {
        this.context = context;
        this.extend = extend == null ? "" : extend;
        this.preferences = context.getSharedPreferences("fixture_spider", Context.MODE_PRIVATE);
        this.preferences.edit()
                .putString("extend", this.extend)
                .putLong("opened", 1700000000000L)
                .commit();
        this.cookie = context.getPackageName() + ":cookie";
        Log.i(TAG, "init from " + context.getFilesDir().getAbsolutePath()
                + (TextUtils.isEmpty(this.extend) ? " (no extend)" : " extend=" + this.extend));
    }

    /** 真实 spider 常见的可选缓存：只有这一条路径需要 SQLite 引擎。 */
    public void openCache() {
        if (cache == null) cache = new Cache(context);
        SQLiteDatabase database = cache.getWritableDatabase();
        database.execSQL("CREATE TABLE IF NOT EXISTS items (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, payload BLOB)");
        ContentValues values = new ContentValues();
        values.put("name", "缓存项");
        values.put("payload", ("cached@" + extend).getBytes(java.nio.charset.StandardCharsets.UTF_8));
        database.insert("items", null, values);
    }

    public int cacheCount() {
        SQLiteDatabase database = cache.getWritableDatabase();
        try (Cursor cursor = database.rawQuery("SELECT COUNT(*) FROM items", null)) {
            cursor.moveToFirst();
            return cursor.getInt(0);
        }
    }

    public int cacheSchemaVersion() {
        return cache.getWritableDatabase().getVersion();
    }

    /** 像真实 spider 一样返回 JSON 文本（这里用 {@code TextUtils} 拼接，验证其真实语义）。 */
    public String homeContent(boolean filter) {
        String categories = TextUtils.join(",", new Object[]{"电影", "剧集", "综艺"});
        List<String> items = new ArrayList<>();
        items.add("{\"vod_id\":\"100\",\"vod_name\":\"" + TextUtils.concat("首页", "项") + "\"}");
        items.add("{\"vod_id\":\"101\",\"vod_name\":\"第二项\"}");
        return "{\"class\":[{\"type_id\":\"1\",\"type_name\":\"" + categories + "\"}],"
                + "\"list\":[" + TextUtils.join(",", items) + "]}";
    }

    public String detailContent(List<String> ids) {
        String id = ids == null || ids.isEmpty() ? "" : ids.get(0);
        return "{\"list\":[{\"vod_id\":\"" + id + "\",\"vod_name\":\"详情\",\"vod_play_url\":\"正片$http://media.invalid/1.m3u8\"}]}";
    }

    /** 真实 spider 会带上持久化的 cookie/UA；这里验证偏好真实落盘。 */
    public String playerContent(String flag, String id, List<String> vipFlags) {
        String stored = preferences.getString("extend", "");
        String header = Base64.encodeToString((cookie + "|" + stored).getBytes(java.nio.charset.StandardCharsets.UTF_8),
                Base64.NO_WRAP);
        return "{\"parse\":0,\"playUrl\":\"\",\"url\":\"http://media.invalid/" + id + ".m3u8\",\"header\":\""
                + header + "\"}";
    }

    public String preference(String key, String fallback) {
        return preferences.getString(key, fallback);
    }

    public Context context() {
        return context;
    }

    /** 真实 plugin 常见的 {@code SQLiteOpenHelper} 子类（放在夹具内部，随夹具一起编译）。 */
    private static final class Cache extends SQLiteOpenHelper {

        Cache(Context context) {
            super(context, "fixture_cache.db", null, 1);
        }

        @Override
        public void onCreate(SQLiteDatabase database) {
            database.execSQL("CREATE TABLE items (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, payload BLOB)");
        }

        @Override
        public void onUpgrade(SQLiteDatabase database, int oldVersion, int newVersion) {
            database.execSQL("DROP TABLE IF EXISTS items");
            onCreate(database);
        }
    }
}
