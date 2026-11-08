using LibVLCSharp.Shared;
using System.Diagnostics;

namespace IPFS;

public class Video : IDisposable
{
    readonly LibVLC libVLC;
    public readonly MediaPlayer MediaPlayer;
    readonly Stopwatch stopwatch = new();
    Media? media = null;
    EventHandler<EventArgs>? playingHandler;

    //EventHandler<MediaParsedChangedEventArgs>? parsedHandler;

    public Video()
    {
        Core.Initialize();
        libVLC = new LibVLC();
        MediaPlayer = new MediaPlayer(libVLC);
    }

    /// <summary>播放 IPFS 网关上发布的流（按 CID 拉取）</summary>
    public void Play(FileData fileData, Action<string>? statusCallback) =>
        PlayCore(new Uri($"{AppConfig.GatewayBaseUrl}/ipfs/{fileData.Cid}"), statusCallback, isStream: true);

    /// <summary>播放本地文件</summary>
    public void Play(string path, Action<string>? statusCallback) =>
        PlayCore(new Uri(path), statusCallback, isStream: false);

    /// <summary>两个入口的共用实现：切换媒体、挂回调、统一异常包装</summary>
    void PlayCore(Uri source, Action<string>? statusCallback, bool isStream)
    {
        try
        {
            if (statusCallback != null) stopwatch.Restart();
            MediaPlayer.Stop();
            DetachHandlers();
            media?.Dispose();
            media = null;
            statusCallback?.Invoke($"开始加载……");

            // 创建新的media
            media = new Media(libVLC, source);
            media.AddOption(":no-video-title-show");//不显示标题
            if (isStream)
            {
                // 流式播放：加大缓存，硬件解码交由 VLC 选择
                media.AddOption(":network-caching=2000");
                media.AddOption(":file-caching=1000");
                media.AddOption(":live-caching=1000");
                media.AddOption(":avcodec-hw=any");//Windows DXVA2
                media.AddOption(":ffmpeg-threads=0");//自动选择线程数
                media.AddOption(":clock-jitter=0");//时钟抖动
                //media.AddOption(":input-fast-seek");//快速定位
                //media.AddOption(":skip-frames");//跳帧以避免延迟
                //media.AddOption(":drop-late-frames");//丢弃延迟帧
                //media.AddOption(":clock-synchro=0");//禁用时钟同步
                //media.AddOption(":rtsp-tcp");//强制RTSP over TCP
                //media.AddOption(":http-reconnect");//自动重连
            }
            else
            {
                media.AddOption(":input-fast-seek");//快速定位
                media.AddOption(":avcodec-hw=dxva2");//Windows DXVA2
                //parsedHandler = (s, e) =>
                //{
                //    statusCallback?.Invoke($"媒体解析完成({stopwatch.ElapsedMilliseconds / 1000}s)");
                //};
                //media.ParsedChanged += parsedHandler;
            }

            // 先订阅再播放，避免首个 Playing 事件早于订阅
            playingHandler = (s, e) =>
            {
                statusCallback?.Invoke($"开始播放({stopwatch.ElapsedMilliseconds / 1000}s)");
                stopwatch.Stop();
            };
            MediaPlayer.Playing += playingHandler;
            //允许网络播放，对本地播放无影响
            media.Parse(MediaParseOptions.ParseNetwork);
            MediaPlayer.Play(media);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "播放失败");
            throw new InvalidOperationException($"播放失败：{ex.Message}", ex);
        }
    }

    /// <summary>切换媒体前解绑上一轮的回调，避免残留订阅</summary>
    void DetachHandlers()
    {
        if (playingHandler != null)
        {
            MediaPlayer.Playing -= playingHandler;
            playingHandler = null;
        }
        //if (parsedHandler != null && media != null)
        //{
        //    media.ParsedChanged -= parsedHandler;
        //    parsedHandler = null;
        //}
    }

    public void Dispose()
    {
        MediaPlayer.Stop();
        media?.Dispose();
        MediaPlayer.Dispose();
        libVLC.Dispose();
        GC.SuppressFinalize(this);
    }
}
