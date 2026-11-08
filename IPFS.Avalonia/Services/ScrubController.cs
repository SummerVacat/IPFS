namespace IPFS.Services;

/// <summary>
/// 进度条拖动与播放器回报之间的仲裁：拖动期间冻结回写；
/// 松手发起 seek 后继续冻结一段时间，直到新位置真正生效，避免滑块弹回。
/// </summary>
public sealed class ScrubController
{
    readonly Func<long> getLength;
    readonly Action<double> applyPosition;
    readonly Action<double> seek;
    readonly TimeSpan guardDuration;
    DateTime guardUntil;

    /// <param name="getLength">媒体总时长（毫秒），为 0 表示未装载媒体</param>
    /// <param name="applyPosition">把滑块值写入界面进度属性</param>
    /// <param name="seek">向播放器发起定位</param>
    public ScrubController(
        Func<long> getLength,
        Action<double> applyPosition,
        Action<double> seek,
        TimeSpan? guardDuration = null)
    {
        this.getLength = getLength;
        this.applyPosition = applyPosition;
        this.seek = seek;
        this.guardDuration = guardDuration ?? TimeSpan.FromMilliseconds(1000);
    }

    /// <summary>是否正在拖动，此间应完全忽略播放器回报</summary>
    public bool IsScrubbing { get; private set; }

    /// <summary>按下：进入拖动状态</summary>
    public void Begin() => IsScrubbing = true;

    /// <summary>松手才跳转，避免网络流媒体在拖动过程中频繁定位</summary>
    public void End(double sliderValue)
    {
        if (!IsScrubbing) return;
        IsScrubbing = false;
        applyPosition(sliderValue);
        //没有加载媒体时不能定位，否则 libvlc 会直接报错
        if (getLength() <= 0) return;
        seek(sliderValue);
        //seek 是异步生效的，期间采样到的仍是旧位置，忽略回写否则滑块会弹回
        guardUntil = DateTime.UtcNow + guardDuration;
    }

    /// <summary>播放器采样的位置是否允许回写到界面</summary>
    public bool TryAcceptSample() => !IsScrubbing && DateTime.UtcNow >= guardUntil;
}
