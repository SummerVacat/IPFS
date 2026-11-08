using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace IPFS.Services;

/// <summary>
/// 播放器抽象：视图模型只依赖该接口，便于替换实现与单元测试。
/// </summary>
public interface IPlaybackService : IDisposable
{
    /// <summary>供 VideoView 渲染的视频输出对象</summary>
    MediaPlayer MediaPlayer { get; }

    /// <summary>是否正在播放</summary>
    bool IsPlaying { get; }

    /// <summary>媒体总时长（毫秒），无媒体时为 0</summary>
    long Length { get; }

    /// <summary>播放进度 0~1，赋值即跳转</summary>
    float Position { get; set; }

    /// <summary>音量 0~100</summary>
    int Volume { get; set; }

    /// <summary>是否静音</summary>
    bool Mute { get; set; }

    /// <summary>播放状态或进度发生变化（在 UI 线程触发）</summary>
    event EventHandler? StateChanged;

    /// <summary>加载或播放失败，参数为错误信息（可能在后台线程触发）</summary>
    event Action<string>? Failed;

    /// <summary>媒体自然播放结束（手动停止不会触发），在 UI 线程触发</summary>
    event EventHandler? Ended;

    /// <summary>播放 IPFS Gateway 上的文件</summary>
    void Play(FileData fileData, Action<string>? statusCallback);

    /// <summary>播放本地文件</summary>
    void Play(string path, Action<string>? statusCallback);

    /// <summary>正在播放则暂停，否则继续播放</summary>
    void TogglePause();

    /// <summary>停止播放</summary>
    void Stop();
}

/// <summary>基于 LibVLC 的播放器实现</summary>
public sealed class VlcPlaybackService : IPlaybackService
{
    /// <summary>
    /// 进度采样间隔：libvlc 的回调在独立线程且频率不可控，
    /// 改为在 UI 线程定时采样，天然避免跨线程更新界面。
    /// </summary>
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    readonly Video video = new();
    readonly DispatcherTimer pollTimer;

    /// <summary>
    /// 播放操作串行化：MediaPlayer.Stop() 与 Media.Parse() 都是同步阻塞调用，
    /// 直接在 UI 线程执行会让界面假死，放在后台线程后还必须防并发切换。
    /// </summary>
    readonly SemaphoreSlim playLock = new(1, 1);

    /// <summary>保证同一时刻只有一次采样在跑</summary>
    int polling;

    //下面这些是采样结果的缓存：界面只读缓存，绝不直接触碰 libvlc
    volatile bool cachedPlaying;
    volatile int cachedVolume = 100;
    volatile bool cachedMute;
    float cachedPosition;
    long cachedLength;

    public VlcPlaybackService()
    {
        pollTimer = new DispatcherTimer { Interval = PollInterval };
        pollTimer.Tick += (_, _) => Poll();
        pollTimer.Start();

        //播完一集后 libvlc 停在 Ended 状态，Play() 不会自己重头再来，需要界面重新加载
        video.MediaPlayer.EndReached += OnEndReached;
    }

    /// <summary>
    /// 该回调在 libvlc 线程触发，此处只能投递，不能直接调用播放器（否则容易死锁）
    /// </summary>
    void OnEndReached(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        cachedPlaying = false;
        cachedPosition = 0;
        StateChanged?.Invoke(this, EventArgs.Empty);
        Ended?.Invoke(this, EventArgs.Empty);
    });

    public MediaPlayer MediaPlayer => video.MediaPlayer;

    public bool IsPlaying => cachedPlaying;

    public long Length => cachedLength;

    public float Position
    {
        get => cachedPosition;
        set
        {
            var target = Math.Clamp(value, 0f, 1f);
            cachedPosition = target;
            Queue(() => video.MediaPlayer.Position = target);
        }
    }

    public int Volume
    {
        get => cachedVolume;
        set
        {
            var target = Math.Clamp(value, 0, 100);
            cachedVolume = target;
            Queue(() => video.MediaPlayer.Volume = target);
        }
    }

    public bool Mute
    {
        get => cachedMute;
        set
        {
            cachedMute = value;
            Queue(() => video.MediaPlayer.Mute = value);
        }
    }

    public event EventHandler? StateChanged;

    public event Action<string>? Failed;

    public event EventHandler? Ended;

    public void Play(FileData fileData, Action<string>? statusCallback)
        => Queue(() => video.Play(fileData, statusCallback));

    public void Play(string path, Action<string>? statusCallback)
        => Queue(() => video.Play(path, statusCallback));

    public void TogglePause() => Queue(() =>
    {
        var player = video.MediaPlayer;
        if (player.IsPlaying) player.Pause();
        else player.Play();
    });

    public void Stop() => Queue(video.MediaPlayer.Stop);

    public void Dispose()
    {
        pollTimer.Stop();
        //MediaPlayer.Stop()/Dispose 同样可能阻塞，放到后台收尾，避免退出时卡住界面
        _ = Task.Run(() =>
        {
            if (!playLock.Wait(TimeSpan.FromSeconds(5))) return;
            try
            {
                video.Dispose();
            }
            catch
            {
                //退出阶段的异常不再上报
            }
            finally
            {
                playLock.Release();
            }
        });
    }

    /// <summary>把阻塞的 libvlc 调用排到后台线程串行执行</summary>
    void Queue(Action action)
    {
        _ = Task.Run(() =>
        {
            playLock.Wait();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "播放操作失败");
                Failed?.Invoke(ex.Message);
            }
            finally
            {
                playLock.Release();
            }
        });
    }

    /// <summary>
    /// 采样播放状态，仅在变化时通知。
    /// libvlc 的属性读取会与播放、解析抢同一把内部锁：本地视频的 Media.Parse 可能阻塞数秒，
    /// 若在 UI 线程读取就会整窗假死，所以这里放到后台线程读，再把结果投回 UI 线程。
    /// </summary>
    void Poll()
    {
        if (Interlocked.Exchange(ref polling, 1) == 1) return;

        _ = Task.Run(() =>
        {
            try
            {
                //与 Stop/Parse/Seek 串行，避免在阻塞操作期间读取属性
                playLock.Wait();

                bool playing;
                float position;
                long length;
                try
                {
                    var player = video.MediaPlayer;
                    playing = player.IsPlaying;
                    position = player.Position;
                    length = player.Length;
                }
                finally
                {
                    playLock.Release();
                }

                Dispatcher.UIThread.Post(() => Publish(playing, position, length));
            }
            catch
            {
                //采样失败忽略，下一次正常继续
            }
            finally
            {
                Interlocked.Exchange(ref polling, 0);
            }
        });
    }

    /// <summary>把采样结果写回缓存并通知界面（UI 线程）</summary>
    void Publish(bool playing, float position, long length)
    {
        if (playing == cachedPlaying
            && length == cachedLength
            && Math.Abs(position - cachedPosition) < 0.001f)
            return;

        cachedPlaying = playing;
        cachedPosition = position;
        cachedLength = length;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
