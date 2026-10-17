using System.Text;

namespace VodBox.Infrastructure;

/// <summary>Java zE.N: LZ-string decompressFromBase64，6-bit alphabet、初始位掩码 32。</summary>
internal static class FtyLzString
{
    private const string Alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";
    internal static string Decode(string input)
    {
        input=input.Replace(" ","",StringComparison.Ordinal);
        if(input.Length==0)return "";
        if(input.Length>8*1024*1024)throw new InvalidDataException("Alllive 压缩响应过大。");
        int index=1,mask=32,value=Digit(input[0]);
        int Bits(int count)
        {
            var result=0;
            for(var bit=0;bit<count;bit++)
            {
                var set=(value&mask)!=0;mask>>=1;
                if(mask==0){mask=32;value=index<input.Length?Digit(input[index]):0;index++;if(index>input.Length+1)throw new InvalidDataException("Alllive 压缩响应截断。");}
                if(set)result|=1<<bit;
            }
            return result;
        }
        var dictionary=new List<string>{"","",""};
        var initial=Bits(2);
        if(initial==2)return "";
        if(initial>2)throw new InvalidDataException("Alllive LZ 起始码无效。");
        var previous=((char)Bits(initial==0?8:16)).ToString();dictionary.Add(previous);
        var output=new StringBuilder(previous);int enlarge=4,width=3;
        while(index<=input.Length)
        {
            var code=Bits(width);
            if(code is 0 or 1){dictionary.Add(((char)Bits(code==0?8:16)).ToString());code=dictionary.Count-1;enlarge--;}
            else if(code==2)return output.ToString();
            if(enlarge==0){enlarge=1<<width;width++;}
            var entry=code<dictionary.Count?dictionary[code]:code==dictionary.Count?previous+previous[0]:throw new InvalidDataException("Alllive LZ 字典码无效。");
            output.Append(entry);if(output.Length>8*1024*1024||dictionary.Count>1024*1024)throw new InvalidDataException("Alllive 解压响应过大。");
            dictionary.Add(previous+entry[0]);enlarge--;previous=entry;
            if(enlarge==0){enlarge=1<<width;width++;if(width>24)throw new InvalidDataException("Alllive LZ 字典过大。");}
        }
        throw new InvalidDataException("Alllive 压缩响应截断。");
    }
    private static int Digit(char c){var value=Alphabet.IndexOf(c);return value<0?throw new InvalidDataException("Alllive LZ 字母表无效。"):value;}
}
