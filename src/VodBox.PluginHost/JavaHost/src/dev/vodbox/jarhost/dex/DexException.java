package dev.vodbox.jarhost.dex;

/**
 * DEX 转换相关失败，携带一个 {@link DexCodes} 分类码。宿主把它翻译成响应信封的
 * {@code errorCode}，上层据此决定「可重试」还是「明确禁用」——不臆造成功。
 */
public final class DexException extends Exception {
    private final String code;

    public DexException(String code, String message) {
        super(message);
        this.code = code;
    }

    public DexException(String code, String message, Throwable cause) {
        super(message, cause);
        this.code = code;
    }

    public String code() {
        return code;
    }
}
