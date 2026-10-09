using Avalonia.Media.Imaging;
using QRCoder;

namespace VodBox.Desktop.Views;

internal static class PairingQr
{
    internal static Bitmap Create(string address)
    {
        if(!Uri.TryCreate(address,UriKind.Absolute,out var uri)||uri.Scheme!="http")throw new ArgumentException("二维码入口地址无效。",nameof(address));
        using var data=QRCodeGenerator.GenerateQrCode(address,QRCodeGenerator.ECCLevel.M);
        using var qr=new PngByteQRCode(data);
        using var stream=new MemoryStream(qr.GetGraphic(6));
        return new Bitmap(stream);
    }
}
