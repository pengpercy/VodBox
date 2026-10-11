package com.github.catvod.crawler;

import android.content.Context;

import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * CatVod 插件（spider）的**宿主公开基类**——VodBox 的独立实现，非上游源码复制。
 *
 * <h2>为什么宿主提供它</h2>
 * {@code dev.vodbox.jarhost.dex.DexArchive} 的 {@code PLATFORM_PREFIXES} 把 {@code com.github.catvod.crawler.}
 * 判定为**宿主已提供的平台类**（插件无需自带、也不该自带）。真实插件在 {@code csp_<Name>} 形态下由宿主
 * 推导出 {@code com.github.catvod.spider.<Name>}（见 {@code Host#deriveSpiderClass}），其外壳类即 {@code extends} 本类。
 * 插件走父加载器优先，因此本类由宿主 {@code host.jar} 提供、对插件**同时**是编译期与运行期契约。
 *
 * <h2>签名依据（按 FTY 真实 bootstrap 核对，不靠命名猜测）</h2>
 * 宿主只通过反射按「方法名 + 形参数」调用 spider，核对 {@code Host.Session} 的真实调用点后，
 * 本类声明的形参顺序/类型与宿主调用**逐一对齐**：
 * <ul>
 *   <li>{@code init(Context)} / {@code init(Context, String)}：宿主 {@code Host#open} 用
 *       {@code findMethod(type, "init", 2)} 以 {@code (context, extend)} 调用；无 {@code extend} 的重载由真实
 *       bootstrap 声明并在默认实现里委派；</li>
 *   <li>{@code homeContent(boolean)}、{@code homeVideoContent()}、{@code categoryContent(String, String, boolean, HashMap)}
 *       —— 宿主分别按 1/0/4 个形参调用，第 4 个参数是筛选表（实参以 {@code Map} 传入，擦除后按 {@code HashMap} 匹配）；</li>
 *   <li>{@code detailContent(List)}、{@code searchContent(String, boolean)} 与 {@code searchContent(String, boolean, String)}
 *       （宿主优先 3 形参、回退 2 形参）、{@code playerContent(String, String, List)} —— 形参数与顺序与宿主调用一致；</li>
 *   <li>{@code liveContent}、{@code action}、{@code proxy(Map)}、{@code manualVideoCheck}、{@code isVideoFormat}、
 *       {@code destroy}：真实契约中的其余可选入口。</li>
 * </ul>
 *
 * <h2>默认值的语义（「可选 → 有意义默认」，绝不虚构媒体）</h2>
 * 所有内容型方法默认返回**空串**，而不是编造的 {@code "{}"} 或占位链接。空串经宿主
 * {@code Session#orEmpty}/{@code #requireContent} 归一后行为是**确定且不伪装**的：
 * <ul>
 *   <li>首页/分类/搜索：空串 → {@code "{}"}（合法的「无结果」）；</li>
 *   <li>详情/播放：空串 → {@code source_empty} **显式失败**（绝不伪装成可播放）。</li>
 * </ul>
 * 布尔与对象型入口返回中性值（{@code false} / {@code null}），语义分别是「无需人工校验」「非视频格式」「不提供本地代理」。
 *
 * <h2>未实现即显式失败</h2>
 * {@link #localProxy(Map)} 与 {@link #proxyAction(String)} **不在**真实的 FTY/TVBox 公开 Spider 契约内（核对
 * 结果：真实契约的本地代理入口是 {@link #proxy(Map)}），而 VodBox 的本地媒体代理属计划中能力（见 README 的 S7）
 * 且宿主从未接线——因此这里**显式抛异常**，而不是返回伪造的代理内容或编造的签名。
 *
 * <h2>边界</h2>
 * 本类只依赖 JDK 与宿主自有的 {@code android.*} 最小子集（宿主 {@code build.sh}/{@code test.sh} 的编译类路径只有
 * JDK + dex-tools）。上游 app 侧的 {@code client()}/{@code safeDns()} 依赖 okhttp3，属宿主运行期不提供的库，
 * 故**不在此声明**（保持宿主 jar 零外部依赖）；需要 HTTP 的插件请自带客户端，与上游行为无冲突。
 */
public abstract class Spider {

    /** 站点标识：由宿主/插件写入，spider 自用（真实契约中的公开字段）。 */
    public String siteKey;

    /** 可选：不带扩展参数的初始化。默认无操作。 */
    public void init(Context context) throws Exception {
    }

    /**
     * 可选：带扩展参数的初始化。默认把 {@code extend} 交给无参形态（与真实契约的委派语义一致），
     * 使只实现 {@link #init(Context)} 的 spider 也能被宿主的 {@code init/2} 调用点命中。
     */
    public void init(Context context, String extend) throws Exception {
        init(context);
    }

    /** 首页分类（可选）。默认空串 → 宿主归一为 {@code "{}"}（无结果）。{@code filter} 是否要求筛选能力。 */
    public String homeContent(boolean filter) throws Exception {
        return "";
    }

    /** 首页推荐视频（可选）。默认空串；宿主仅在非空时才与首页结果合并。 */
    public String homeVideoContent() throws Exception {
        return "";
    }

    /**
     * 分类列表（可选）。默认空串 → 宿主归一为 {@code "{}"}。
     *
     * @param tid    分类 id
     * @param pg     页码（宿主保证非空，默认 "1"）
     * @param filter 是否启用筛选
     * @param extend 筛选条件（宿主以 {@code Map} 实参传入，形参按 {@code HashMap} 接受）
     */
    public String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) throws Exception {
        return "";
    }

    /**
     * 详情（可选）。默认空串 → 宿主按 {@code source_empty} **显式失败**（不返回伪装成可播放的内容）。
     */
    public String detailContent(List<String> ids) throws Exception {
        return "";
    }

    /** 搜索（可选，2 形参形态）。默认空串 → 宿主归一为 {@code "{}"}（搜索源合法返回空）。 */
    public String searchContent(String key, boolean quick) throws Exception {
        return "";
    }

    /**
     * 搜索（可选，带翻页的 3 形参形态）。默认委派 {@link #searchContent(String, boolean)}，
     * 与真实契约一致：只实现 2 形参的 spider 也能被宿主的 {@code searchContent/3} 调用点命中。
     */
    public String searchContent(String key, boolean quick, String pg) throws Exception {
        return searchContent(key, quick);
    }

    /**
     * 播放地址（可选）。默认空串 → 宿主按 {@code source_empty} **显式失败**（不虚构播放地址）。
     *
     * @param flag     线路标识
     * @param id       剧集 id
     * @param vipFlags 需要解析的 flag 列表
     */
    public String playerContent(String flag, String id, List<String> vipFlags) throws Exception {
        return "";
    }

    /** 直播内容（可选）。默认空串（无直播）。 */
    public String liveContent(String url) throws Exception {
        return "";
    }

    /** 自定义动作（可选）。默认空串（无动作结果）。 */
    public String action(String action) throws Exception {
        return "";
    }

    /**
     * 本地代理入口（真实契约中的本地代理钩子）：默认 {@code null}，语义为「本 spider 不提供本地代理」，
     * 是中性且不伪造的默认；需要代理的 spider 覆写它返回 {@code Object[]} 响应三元组。
     */
    public Object[] proxy(Map<String, String> params) throws Exception {
        return null;
    }

    /** 是否需要人工校验视频（可选）。默认 {@code false}。 */
    public boolean manualVideoCheck() {
        return false;
    }

    /** 是否为视频格式（可选）。默认 {@code false}（不臆断）。 */
    public boolean isVideoFormat(String url) {
        return false;
    }

    /** 释放资源（可选）。默认无操作。 */
    public void destroy() {
    }

    /**
     * ⚠️ **不在**真实 FTY/TVBox 公开 Spider 契约内（核对结果：本地代理入口是 {@link #proxy(Map)}），
     * 且宿主**从未**通过它取内容；VodBox 的本地媒体代理仍是计划中能力（README 的 S7）。
     * 为让误用得到**显式可诊断**的失败而非静默的伪造结果，这里恒抛异常。
     *
     * @throws UnsupportedOperationException 恒抛出：契约未声明该入口，宿主也未接线本地媒体代理。
     */
    public Object[] localProxy(Map<String, String> params) throws Exception {
        throw new UnsupportedOperationException(
                "localProxy 不在真实 Spider 公开契约内（本地代理入口是 proxy(Map)），VodBox 尚未接线本地媒体代理");
    }

    /**
     * ⚠️ **不在**真实 FTY/TVBox 公开 Spider 契约内（核对结果：动作入口是 {@link #action(String)}）。
     * 同 {@link #localProxy(Map)}：恒抛异常，绝不返回编造的动作结果。
     *
     * @throws UnsupportedOperationException 恒抛出：契约未声明该入口。
     */
    public String proxyAction(String action) throws Exception {
        throw new UnsupportedOperationException(
                "proxyAction 不在真实 Spider 公开契约内（动作入口是 action(String)），VodBox 未声明该入口");
    }
}
