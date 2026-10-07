using System.Text;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public class StegoConfigTests
{
    [Fact]
    public void DecodesJpegTailBase64WithPrefixNoise()
    {
        // 构造：JPEG 头 + EOI + 前缀噪声 + "**" + base64(JSON)
        const string json = "{\"sites\":[{\"key\":\"a\",\"name\":\"A站\",\"api\":\"csp_X\"},{\"key\":\"m\",\"name\":\"M站\",\"api\":\"https://m.com/api.php/provide/vod\"}]}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 }
            .Concat(Encoding.ASCII.GetBytes("\xFF\xD9"))
            .Concat(Encoding.ASCII.GetBytes("slMDkXRn**" + b64))
            .ToArray();
        var config = ConfigLoader.ParseBytes(bytes);
        Assert.NotNull(config);
        Assert.Equal(2, config!.Sites.Count);
    }

    [Fact]
    public void StripsCommentedSiteLines()
    {
        const string json = """
        {
        "sites":[
        {"key":"a","name":"A站","type":0,"api":"https://a.com/api.php/provide/vod"},
        //{"key":"海绵","name":"海绵","type":3,"api":"csp_X"},
        {"key":"b","name":"B站","type":0,"api":"https://b.com/api.php/provide/vod"}]
        }
        """;
        var config = ConfigLoader.Parse(json);
        Assert.NotNull(config);
        Assert.Equal(2, config!.Sites.Count); // 注释行被剥掉
        Assert.DoesNotContain(config.Sites, s => s.Key == "海绵");
    }

    [Fact]
    public void StripCommentsKeepsHttpsUrls()
    {
        const string json = """
        {
        "sites":[{"key":"a","name":"A","api":"https://a.com/api.php/provide/vod","ext":"https://e.com/x.json"}]
        }
        """;
        var config = ConfigLoader.Parse(json);
        Assert.NotNull(config);
        // https:// 中的 // 不能被误剥
        Assert.Equal("https://a.com/api.php/provide/vod", config!.Sites[0].Api);
    }

    [Fact]
    public void ReturnsNullForPlainImage()
    {
        // 纯图片（无 base64 尾巴） → null
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0xFF, 0xD9 };
        Assert.Null(ConfigLoader.ParseBytes(bytes));
    }
}
