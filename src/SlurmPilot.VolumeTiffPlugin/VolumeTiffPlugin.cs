using System.Windows;
using SlurmPilot.Plugin.Abstractions;

namespace SlurmPilot.VolumeTiffPlugin;

public sealed class VolumeTiffPlugin : ISlurmPilotPlugin, IDisposable
{
    private VolumeViewerControl? _view;
    public string Id => "slurmpilot.volume-tiff";
    public string DisplayName => "三维影像";
    public string Description => "读取 8/16 位单通道多页 TIFF 或 high/low 双 MP4，并使用 GPU 体渲染显示。";
    public string Icon => "3D";
    public int Order => 10;

    public FrameworkElement CreateView(IPluginContext context)
    {
        context.Log(PluginLogLevel.Information, "Creating the volume TIFF viewer on demand.");
        _view?.Dispose();
        _view = new VolumeViewerControl(context);
        return _view;
    }

    public void OnUnloading()
    {
        _view?.Dispose();
        _view = null;
    }

    public void Dispose() => OnUnloading();
}
