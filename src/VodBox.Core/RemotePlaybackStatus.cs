namespace VodBox.Core;

/// <summary>遥控只读状态，不暴露媒体URL/请求头/授权信息。</summary>
public sealed record RemotePlaybackStatus(string Title,string State,double PositionSeconds,double DurationSeconds,int Volume,double Rate);
