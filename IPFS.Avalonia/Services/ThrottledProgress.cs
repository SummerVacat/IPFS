namespace IPFS.Services;

/// <summary>
/// 进度去重包装：值与上次相同的不再上报，避免高频进度把界面刷爆。
/// null 忽略、相同值忽略。
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    static readonly EqualityComparer<T> Comparer = EqualityComparer<T>.Default;

    readonly IProgress<T> inner;
    readonly object gate = new();
    T? previous;
    bool hasPrevious;

    public ThrottledProgress(IProgress<T> inner) => this.inner = inner;

    public void Report(T value)
    {
        if (value is null) return;
        lock (gate)
        {
            if (hasPrevious && Comparer.Equals(previous, value)) return;
            previous = value;
            hasPrevious = true;
            inner.Report(value);
        }
    }
}
