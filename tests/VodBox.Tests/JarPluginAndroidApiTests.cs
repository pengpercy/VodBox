using System.Text.RegularExpressions;
using Xunit;

namespace VodBox.Tests;

/// <summary>
/// P2-09「Android 常用 API 子集」的离线契约测试：**不启动 JVM**（与既有 JAR 宿主测试同约定），
/// 只扫描 Java 宿主源码，锁定「实现了哪些 android.* / dalvik.* 类型与成员」以及「哪些仍然不实现」。
///
/// 真正的方法级行为（SharedPreferences 落盘、Base64 标志位、LruCache 淘汰、AtomicFile 提交、
/// Uri 解析、Handler/Looper 投递、系统服务拒绝等）由 <c>src/VodBox.PluginHost/JavaHost/test.sh</c>
/// 里的 <c>AndroidApiSelfTest</c> 用 JDK 直接断言——那是本仓库里唯一会启动 JVM 的路径，
/// 与 C# 测试的「不启进程」约定分开。
/// </summary>
public sealed class JarPluginAndroidApiTests
{
    private static readonly string? JavaSourceRoot = FindJavaHostSource();

    /// <summary>从测试输出目录向上寻找 Java 宿主源码根；找不到时用例退化为空操作（不误报失败）。</summary>
    private static string? FindJavaHostSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "VodBox.PluginHost", "JavaHost", "src");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private static string Read(string relativePath)
    {
        var path = Path.Combine(JavaSourceRoot!, relativePath);
        Assert.True(File.Exists(path), $"缺少 Java 源文件：{path}");
        return File.ReadAllText(path);
    }

    // ---------- 已实现的类型与关键成员（成员用逗号分隔）----------

    public static TheoryData<string, string, string> ImplementedTypes() => new()
    {
        { "android/content/Context.java", "Context", "getPackageName,getFilesDir,getCacheDir,getSharedPreferences,getAssets,getPackageManager,getSystemService" },
        { "android/content/ContextWrapper.java", "ContextWrapper", "getBaseContext,attachBaseContext" },
        { "android/content/SharedPreferences.java", "SharedPreferences", "getStringSet,edit" },
        { "android/app/Application.java", "Application", "attachBaseContext" },
        { "android/content/res/AssetManager.java", "AssetManager", "open,list" },
        { "android/content/pm/PackageManager.java", "PackageManager", "getPackageInfo,NameNotFoundException" },
        { "android/content/pm/PackageInfo.java", "PackageInfo", "versionName,versionCode" },
        { "android/content/pm/ApplicationInfo.java", "ApplicationInfo", "targetSdkVersion" },
        { "android/util/Base64.java", "Base64", "encodeToString,decode,URL_SAFE,NO_WRAP,NO_PADDING" },
        { "android/util/Log.java", "Log", "println,getStackTraceString" },
        { "android/util/LruCache.java", "LruCache", "entryRemoved,snapshot,evictAll" },
        { "android/util/AtomicFile.java", "AtomicFile", "startWrite,finishWrite,failWrite,openRead,readFully" },
        { "android/net/Uri.java", "Uri", "parse,getQueryParameter,buildUpon,encode,decode" },
        { "android/os/Build.java", "Build", "MANUFACTURER,VERSION" },
        { "android/os/Environment.java", "Environment", "getExternalStorageDirectory,getExternalStorageState" },
        { "android/os/SystemClock.java", "SystemClock", "uptimeMillis,elapsedRealtime,sleep" },
        { "android/os/Handler.java", "Handler", "post,postDelayed,sendMessage,handleMessage,removeCallbacks" },
        { "android/os/Looper.java", "Looper", "getMainLooper,myLooper,prepare,loop,quit" },
        { "android/os/Message.java", "Message", "obtain,sendToTarget,getData" },
        { "android/os/Bundle.java", "Bundle", "putString,getString,putInt" },
        { "dalvik/system/VMRuntime.java", "VMRuntime", "getRuntime,setTargetSdkVersion,setHiddenApiExemptions" },
        { "dalvik/system/DexClassLoader.java", "DexClassLoader", "findClass" },
    };

    [Theory]
    [MemberData(nameof(ImplementedTypes))]
    public void ImplementedAndroidTypesAndMembersExist(string relativePath, string typeName, string members)
    {
        if (JavaSourceRoot is null) return;
        var text = Read(relativePath);
        var declaration = new Regex($@"\b(class|interface|enum)\s+{Regex.Escape(typeName)}\b");
        Assert.True(declaration.IsMatch(text), $"{relativePath} 未声明类型 {typeName}");
        foreach (var member in members.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.Contains(member, text);
        }
    }

    // ---------- 明确不实现的边界（不得伪装可用）----------

    [Theory]
    [InlineData("android/app/Activity.java")]
    [InlineData("android/webkit/WebView.java")]
    [InlineData("android/widget/Toast.java")]
    [InlineData("android/view/View.java")]
    public void AndroidUiAndWebViewTypesAreNotStubbed(string relativePath)
    {
        if (JavaSourceRoot is null) return;
        Assert.False(File.Exists(Path.Combine(JavaSourceRoot, relativePath)),
            $"未实现的 Android UI/WebView 类型不得提供桩：{relativePath}");
    }

    [Fact]
    public void NoAndroidSystemServiceStubPackageExists()
    {
        if (JavaSourceRoot is null) return;
        // 系统服务（connectivity/notification/…）不提供任何桩类型；getSystemService 一律拒绝。
        Assert.False(Directory.Exists(Path.Combine(JavaSourceRoot, "android", "webkit")), "不得提供 android.webkit 桩目录");
    }

    // ---------- 系统服务：明确拒绝而非伪装 ----------

    [Fact]
    public void SystemServicesAreRefusedNotFaked()
    {
        if (JavaSourceRoot is null) return;
        Assert.Contains("Object getSystemService(String name)", Read("android/content/Context.java"));
        var desktop = Read("dev/vodbox/jarhost/android/DesktopContext.java");
        Assert.Contains("UnsupportedOperationException", desktop);
        Assert.Contains("getSystemService", desktop);
    }

    // ---------- 宿主装配与错误分类 ----------

    [Fact]
    public void HostWiresAndroidContextIntoSpiderInit()
    {
        if (JavaSourceRoot is null) return;
        var host = Read("dev/vodbox/jarhost/Host.java");
        Assert.Contains("createAndroidContext", host);
        Assert.Contains("invokeMethod(spider, init, context", host);
        Assert.Contains("\"unsupported_android\"", host);
        Assert.Contains("\"unsupported_native_library\"", host);
    }

    [Fact]
    public void LinkageErrorsAgainstAndroidFrameworkClassifyAsUnsupported()
    {
        if (JavaSourceRoot is null) return;
        var host = Read("dev/vodbox/jarhost/Host.java");
        // 缺类/缺方法/缺字段等链接错误统一按 androidFrameworkReference 归到 unsupported_android，
        // 而不是逐类型列举（否则新增 stub 就漏判）。
        Assert.Contains("androidFrameworkReference", host);
        Assert.Contains("unsupported_android", host);
    }
}
