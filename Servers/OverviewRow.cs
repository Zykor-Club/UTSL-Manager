using System.ComponentModel;
using Avalonia.Media;

namespace UTSLManager.Servers;

/// <summary>多档概览的一行。属性带 INPC：采样是每 10 秒就地更新，不能让整表重建（会闪烁、也会丢滚动位置）。</summary>
public sealed class OverviewRow : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>本机 / 远端（远端再细分 隧道 or 直连）。</summary>
    public string Mode { get; init; } = "";

    private bool _online;
    private string _stateText = "…";
    private string _worldsText = "—";
    private string _cpuText = "—";
    private string _memText = "—";
    private string _latText = "—";
    private string _note = "";

    public bool Online { get => _online; set { if (_online == value) { return; } _online = value; Raise(nameof(Online)); Raise(nameof(DotBrush)); } }
    public string StateText { get => _stateText; set => Set(ref _stateText, value, nameof(StateText)); }
    public string WorldsText { get => _worldsText; set => Set(ref _worldsText, value, nameof(WorldsText)); }
    public string CpuText { get => _cpuText; set => Set(ref _cpuText, value, nameof(CpuText)); }
    public string MemText { get => _memText; set => Set(ref _memText, value, nameof(MemText)); }
    public string LatText { get => _latText; set => Set(ref _latText, value, nameof(LatText)); }
    /// <summary>出错/降级时的说明（隧道没建、超时等），正常时为空。</summary>
    public string Note { get => _note; set { if (_note == value) { return; } _note = value; Raise(nameof(Note)); Raise(nameof(HasNote)); } }
    public bool HasNote => _note.Length > 0;

    /// <summary>状态点颜色：在线绿、不可达灰（不用转换器，直接给 Brush）。</summary>
    public IBrush DotBrush => Online
        ? new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#30D158"))
        : new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#98989F"));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, string name)
    {
        if (field == value) { return; }
        field = value ?? "";
        Raise(name);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
