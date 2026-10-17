using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace VodBox.Infrastructure;

/// <summary>Appgz RSA/PKCS1 + AES-CBC 信封，来自 Java Appgz.N/tF、merge.Xu。</summary>
internal static class AppGzCrypto
{
    internal const string PublicKey="MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDUM5+/y8sPsWkd1/RQS64X259EUwxFXFE5HlA65MqrxnPs0JqoSRojSDy5QhwvROlaD6TwRQHKMY2OAZ6SnQeUJsChTEFIR9qUkwrs3/MVUMxjsv6JS6Oe/juclyJGTgVmDhB55EafXsD0SQYVj/QXXsxR6ewR5E2kL52yAAD4yQIDAQAB";
    internal const string PrivateKey="MIICdgIBADANBgkqhkiG9w0BAQEFAASCAmAwggJcAgEAAoGAe6hKrWLi1zQmjTT1ozbE4QdFeJGNxubxld6GrFGximxfMsMB6BpJhpcTouAqywAFppiKetUBBbXwYsYU1wNr648XVmPmCMCy4rY8vdliFnbMUj086DU6Z+/oXBdWU3/b1G0DN3E9wULRSwcKZT3wj/cCI1vsCm3gj2R5SqkA9Y0CAwEAAQKBgAJH+4CxV0/zBVcLiBCHvSANm0l7HetybTh/j2p0Y1sTXro4ALwAaCTUeqdBjWiLSo9lNwDHFyq8zX90+gNxa7c5EqcWV9FmlVXr8VhfBzcZo1nXeNdXFT7tQ2yah/odtdcx+vRMSGJd1t/5k5bDd9wAvYdIDblMAg+wiKKZ5KcdAkEA1cCakEN4NexkF5tHPRrR6XOY/XHfkqXxEhMqmNbB9U34saTJnLWIHC8IXys6Qmzz30TtzCjuOqKRRy+FMM4TdwJBAJQZFPjsGC+RqcG5UvVMiMPhnwe/bXEehShK86yJK/g/UiKrO87h3aEu5gcJqBygTq3BBBoH2md3pr/W+hUMWBsCQQChfhTIrdDinKi6lRxrdBnn0Ohjg2cwuqK5zzU9p/N+S9x7Ck8wUI53DKm8jUJE8WAG7WLj/oCOWEh+ic6NIwTdAkEAj0X8nhx6AXsgCYRql1klbqtVmL8+95KZK7PnLWG/IfjQUy3pPGoSaZ7fdquG8bq8oyf5+dzjE/oTXcByS+6XRQJAP/5ciy1bL3NhUhsaOVy55MHXnPjdcTX0FaLi+ybXZIfIQ2P4rb19mVq1feMbCXhz+L1rG8oat5lYKfpe8k83ZA==";
    internal static string Json(IReadOnlyDictionary<string,string> value)=>Json(value.ToDictionary(pair=>pair.Key,pair=>(object?)pair.Value));
    internal static string Json(IReadOnlyDictionary<string,object?> value)
    {
        using var stream=new MemoryStream();using(var writer=new Utf8JsonWriter(stream,new JsonWriterOptions{Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}))
        {
            writer.WriteStartObject();foreach(var pair in value)
            {
                writer.WritePropertyName(pair.Key);switch(pair.Value)
                {
                    case null:writer.WriteNullValue();break;
                    case string text:writer.WriteStringValue(text);break;
                    case int number:writer.WriteNumberValue(number);break;
                    case long longNumber:writer.WriteNumberValue(longNumber);break;
                    case JsonElement element:element.WriteTo(writer);break;
                    default:throw new InvalidDataException("Appgz JSON 字段类型无效。");
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    internal static string RandomKey()=>new string(Enumerable.Range(0,16).Select(_=>"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"[RandomNumberGenerator.GetInt32(62)]).ToArray()).ToUpperInvariant();
    internal static Dictionary<string,string> Wrap(string json,string token,long timestamp,string key,string iv,string phone="vodbox-desktop",Func<string,string>? rsaEncrypt=null)
    {
        using var aes=Aes.Create();aes.Key=Encoding.UTF8.GetBytes(key);aes.IV=Encoding.UTF8.GetBytes(iv);aes.Mode=CipherMode.CBC;aes.Padding=PaddingMode.PKCS7;
        var request=Convert.ToHexString(aes.EncryptCbc(Encoding.UTF8.GetBytes(json),aes.IV,PaddingMode.PKCS7));
        var secret=Json(new Dictionary<string,string>{{"iv",iv},{"key",key}});
        var keys=(rsaEncrypt??Encrypt)(secret);var time=timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var canonical="token_id=,token="+token+",phone_type=1,request_key="+request+",app_id=1,time="+time+",keys="+keys+"*&zvdvdvddbfikkkumtmdwqppp?|4Y!s!2br";
        var signature=Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(){{"token",token},{"token_id",""},{"phone_type","1"},{"time",time},{"phone_model",phone},{"keys",keys},{"request_key",request},{"signature",signature},{"app_id","1"},{"ad_version","1"}};
    }
    private static string Encrypt(string value)
    {
        using var rsa=RSA.Create();rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey),out _);
        var input=Encoding.UTF8.GetBytes(value);using var result=new MemoryStream();
        for(var offset=0;offset<input.Length;offset+=117)result.Write(rsa.Encrypt(input.AsSpan(offset,Math.Min(117,input.Length-offset)).ToArray(),RSAEncryptionPadding.Pkcs1));
        return Convert.ToBase64String(result.ToArray());
    }
    internal static JsonDocument Unwrap(string value,Func<string,string>? rsaDecrypt=null)
    {
        using var doc=JsonDocument.Parse(value);var data=doc.RootElement.GetProperty("data");var encryptedKeys=data.GetProperty("keys").GetString()??throw new InvalidDataException("Appgz 缺少 RSA keys。");
        using var secret=JsonDocument.Parse((rsaDecrypt??Decrypt)(encryptedKeys));
        var key=Encoding.UTF8.GetBytes(secret.RootElement.GetProperty("key").GetString()??"");var iv=Encoding.UTF8.GetBytes(secret.RootElement.GetProperty("iv").GetString()??"");
        var input=Convert.FromHexString((data.GetProperty("response_key").GetString()??"").Replace("\\","",StringComparison.Ordinal));
        using var aes=Aes.Create();aes.Key=key;
        return JsonDocument.Parse(aes.DecryptCbc(input,iv,PaddingMode.PKCS7));
    }
    private static string Decrypt(string value)
    {
        var input=Convert.FromBase64String(value);if(input.Length>1024*1024||input.Length%128!=0)throw new InvalidDataException("Appgz RSA 响应长度无效。");
        using var rsa=RSA.Create();rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(PrivateKey),out _);using var result=new MemoryStream();
        for(var offset=0;offset<input.Length;offset+=128)result.Write(rsa.Decrypt(input.AsSpan(offset,128).ToArray(),RSAEncryptionPadding.Pkcs1));
        return Encoding.UTF8.GetString(result.ToArray());
    }
}
