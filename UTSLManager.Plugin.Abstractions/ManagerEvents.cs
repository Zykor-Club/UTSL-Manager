using System;
using System.Collections.Generic;

namespace UTSLManager.Plugins;

/// <summary>
/// 管理器事件总线（每次插件加载/重载产生一个新实例：旧实例连同其订阅一起被丢弃，
/// 这样卸载插件后不会有残留的委托把插件钉在内存里）。
/// 所有 Raise 都吞异常并回报，保证单个插件出错不会影响管理器或其他插件。
///
/// ★ Raise* 是 internal：本程序集通过 InternalsVisibleTo("UTSLManager") 只授权宿主触发 ——
///   插件只能订阅，不能伪造"服务端上线/世界切换"这类事件。
/// </summary>
public sealed class ManagerEvents
{
    private readonly Action<string>? _error;

    public ManagerEvents(Action<string>? error = null) => _error = error;

    /// <summary>服务端上线/下线。</summary>
    public event Action<bool>? ServerStateChanged;
    /// <summary>当前选中的世界改变（值为世界名，"全部世界" 也会触发）。</summary>
    public event Action<string>? WorldChanged;
    /// <summary>世界列表刷新。</summary>
    public event Action<IReadOnlyList<WorldInfo>>? WorldListRefreshed;
    /// <summary>监控采样（约 2 秒一次）。</summary>
    public event Action<MetricsSample>? MetricsSampled;
    /// <summary>终端新增一行。</summary>
    public event Action<TerminalLineEvent>? TerminalLine;
    /// <summary>管理器正在关闭（插件应在此保存状态、停止后台任务）。</summary>
    public event Action? ShuttingDown;

    /// <summary>是否有插件订阅了终端行（没有时 Term 会跳过，零开销）。</summary>
    public bool HasTerminalLineSubscribers => TerminalLine is not null;

    internal void RaiseServerStateChanged(bool up) => SafeEach("ServerStateChanged", ServerStateChanged, up);
    internal void RaiseWorldChanged(string world) => SafeEach("WorldChanged", WorldChanged, world);
    internal void RaiseWorldListRefreshed(IReadOnlyList<WorldInfo> worlds) => SafeEach("WorldListRefreshed", WorldListRefreshed, worlds);
    internal void RaiseMetricsSampled(MetricsSample sample) => SafeEach("MetricsSampled", MetricsSampled, sample);
    internal void RaiseTerminalLine(TerminalLineEvent line) => SafeEach("TerminalLine", TerminalLine, line);
    internal void RaiseShuttingDown() => SafeEach0("ShuttingDown", ShuttingDown);

    // ★ 修 bug#8：多播委托遇到第一个抛异常的订阅者会中断后续订阅者，
    //   必须用 GetInvocationList 逐个 try，才能兑现「单个插件出错不影响其他插件」的承诺。
    private void SafeEach<T>(string what, Action<T>? evt, T arg)
    {
        if (evt is null) { return; }
        foreach (Delegate d in evt.GetInvocationList())
        {
            try { ((Action<T>)d)(arg); }
            catch (Exception ex) { _error?.Invoke($"[插件事件] {what} 处理失败: {ex.Message}"); }
        }
    }

    private void SafeEach0(string what, Action? evt)
    {
        if (evt is null) { return; }
        foreach (Delegate d in evt.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception ex) { _error?.Invoke($"[插件事件] {what} 处理失败: {ex.Message}"); }
        }
    }
}
