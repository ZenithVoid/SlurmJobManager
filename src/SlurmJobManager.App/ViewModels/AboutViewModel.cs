using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.Services;
using SlurmJobManager.App.Services.Updates;
using SlurmJobManager.Core.Services;

namespace SlurmJobManager.App.ViewModels;

public sealed class AboutViewModel : ViewModelBase
{
    private const string RepositoryUrl = "https://github.com/ZenithVoid/SlurmJobManager";
    private const string MaintainerFallback = "ZenithVoid";
    private const string ProductNameFallback = "Slurm Job Manager";
    private readonly Action _checkUpdatesAction;

    public AboutViewModel(IApplicationVersionService versionService, Action checkUpdatesAction)
    {
        if (versionService is null) throw new ArgumentNullException(nameof(versionService));
        _checkUpdatesAction = checkUpdatesAction ?? throw new ArgumentNullException(nameof(checkUpdatesAction));

        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var titleFromResources = Application.Current?.TryFindResource("App.Title") as string;
        var productFromAssembly = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
        var companyFromAssembly = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        var copyrightFromAssembly = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;

        ProductName = FirstNonEmpty(titleFromResources, productFromAssembly, ProductNameFallback);
        var maintainer = FirstNonEmpty(companyFromAssembly, MaintainerFallback);
        Maintainer = maintainer;
        Organization = maintainer;
        CopyrightNotice = FirstNonEmpty(copyrightFromAssembly, $"© {DateTime.Now.Year} {maintainer}");
        CurrentVersionDisplay = FormatVersion(versionService.CurrentVersionDisplay);
        RepositoryAddress = RepositoryUrl;
        LicenseDisplay = "Apache License 2.0";
        LogsDirectory = LocalDataPaths.LogsDirectory;

        OpenRepositoryCommand = new RelayCommand(() => OpenPathOrUrl(RepositoryAddress));
        OpenLogsDirectoryCommand = new RelayCommand(OpenLogsDirectory);
        CheckUpdatesCommand = new RelayCommand(() => _checkUpdatesAction());
    }

    public string ProductName { get; }
    public string CurrentVersionDisplay { get; }
    public string Maintainer { get; }
    public string Organization { get; }
    public string CopyrightNotice { get; }
    public string RepositoryAddress { get; }
    public string LicenseDisplay { get; }
    public string LogsDirectory { get; }

    public ICommand OpenRepositoryCommand { get; }
    public ICommand OpenLogsDirectoryCommand { get; }
    public ICommand CheckUpdatesCommand { get; }

    private void OpenLogsDirectory()
    {
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            OpenPathOrUrl(LogsDirectory);
        }
        catch (Exception ex)
        {
            ToastService.Instance.Error(string.Format(L("About.OpenLogsFailedFormat"), ex.Message));
        }
    }

    private static void OpenPathOrUrl(string pathOrUrl)
    {
        try
        {
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = pathOrUrl,
                UseShellExecute = true,
            });

            if (started == null)
                ToastService.Instance.Error(L("About.OpenTargetFailed"));
        }
        catch (Exception ex)
        {
            ToastService.Instance.Error(string.Format(L("About.OpenTargetFailedFormat"), ex.Message));
        }
    }

    private static string FormatVersion(string input)
    {
        var trimmed = input?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
            return "v0.0.0";
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return $"v{trimmed}";
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "-";

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
