﻿using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IPFS.ViewModels;

namespace IPFS.Services;

/// <summary>
/// 运行状态与日志：页面状态行与诊断日志列表的唯一来源。
/// 所有更新经构造函数注入的 dispatch 投递，订阅界面因此总在 UI 线程收到；
/// 注入 null 时同步执行，便于测试与控制台场景。
/// </summary>
public sealed partial class StatusLogService : ObservableObject, IStatusLogService
{
    /// <summary>日志保留条数上限，避免长时间运行内存膨胀</summary>
    const int MaxEntries = 500;

    readonly Action<Action>? dispatch;

    /// <summary>最近一次状态文本，页面状态行绑定它</summary>
    [ObservableProperty]
    private string status = "就绪";

    /// <summary>按时间顺序显示的日志行，已带时间前缀</summary>
    public ObservableCollection<string> LogEntries { get; } = [];

    /// <summary>供业务层上报进度的节流接口</summary>
    public IProgress<string> Progress { get; }

    /// <summary>全部日志行按系统换行拼接，供「复制全部」使用</summary>
    public string AllText => string.Join(Environment.NewLine, LogEntries);

    public StatusLogService(Action<Action>? dispatch = null)
    {
        this.dispatch = dispatch;
        //在创建时所在线程（UI 线程）捕获同步上下文，进度回调因此自动封送回界面
        Progress = new ThrottledProgress<string>(new Progress<string>(Append));
    }

    /// <summary>只改状态栏，不写日志</summary>
    public void SetStatus(string message) => Dispatch(() => Status = message);

    /// <summary>追加一条日志，同时把状态栏更新为该消息</summary>
    public void Append(string message)
    {
        //时间与格式在调用线程确定，集合只在 UI 线程改动
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
        Dispatch(() =>
        {
            LogEntries.Add(line);
            if (LogEntries.Count > MaxEntries)
                LogEntries.RemoveAt(0);
            Status = message;
        });
    }

    void Dispatch(Action action)
    {
        if (dispatch is null) action();
        else dispatch(action);
    }
}
