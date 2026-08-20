using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SlurmPilot.Plugin.Abstractions;

namespace SlurmPilot.SamplePlugin;

public sealed class SamplePlugin : ISlurmPilotPlugin
{
    public string Id => "slurmpilot.sample";
    public string DisplayName => "示例插件";
    public string Description => "验证运行时插件发现、加载与页面调用。";
    public string Icon => "S";
    public int Order => 100;

    public FrameworkElement CreateView(IPluginContext context)
    {
        context.Log(PluginLogLevel.Information, "Creating the sample plugin view.");

        var panel = new StackPanel { Margin = new Thickness(28) };
        var title = new TextBlock
        {
            Text = "SlurmPilot 示例插件",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(title);
        var description = new TextBlock
        {
            Text = "这个页面来自 plugin 文件夹中的独立程序集，而不是主程序内置页面。",
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(description);
        var version = new TextBlock
        {
            Text = $"宿主版本：{context.HostVersion}",
            Margin = new Thickness(0, 18, 0, 0),
        };
        version.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(version);
        var directory = new TextBlock
        {
            Text = $"插件目录：{context.PluginDirectory}",
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        directory.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(directory);

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }
}
