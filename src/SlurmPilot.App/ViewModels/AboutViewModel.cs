using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using SlurmPilot.App.Services;
using SlurmPilot.App.Services.ExternalTargets;
using SlurmPilot.App.Services.Updates;
using SlurmPilot.Core.Services;

namespace SlurmPilot.App.ViewModels;

public sealed class AboutViewModel : ViewModelBase
{
    private const string RepositoryUrl = "https://github.com/ZenithVoid/SlurmPilot";
    private const string CopyrightNoticeFileName = "COPYRIGHT";
    private const string ThirdPartyNoticesFileName = "THIRD_PARTY_NOTICES.md";
    private const string MaintainerFallback = "ZenithVoid";
    private const string ProductNameFallback = "SlurmPilot";
    private readonly Action _checkUpdatesAction;
    private readonly IExternalTargetOpener _externalTargetOpener;

    public AboutViewModel(
        IApplicationVersionService versionService,
        IExternalTargetOpener externalTargetOpener,
        Action checkUpdatesAction)
    {
        if (versionService is null) throw new ArgumentNullException(nameof(versionService));
        _externalTargetOpener = externalTargetOpener ?? throw new ArgumentNullException(nameof(externalTargetOpener));
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
        CopyrightNoticeFilePath = Path.Combine(AppContext.BaseDirectory, CopyrightNoticeFileName);
        ThirdPartyNoticesFilePath = Path.Combine(AppContext.BaseDirectory, ThirdPartyNoticesFileName);

        OpenRepositoryCommand = new RelayCommand(() => OpenPathOrUrl(RepositoryAddress));
        OpenLogsDirectoryCommand = new RelayCommand(OpenLogsDirectory);
        CheckUpdatesCommand = new RelayCommand(() => _checkUpdatesAction());
        OpenCopyrightNoticeCommand = new RelayCommand(() => OpenBundledDocument(CopyrightNoticeFilePath, "About.CopyrightNoticeMissingFormat"));
        OpenThirdPartyNoticesCommand = new RelayCommand(() => OpenBundledDocument(ThirdPartyNoticesFilePath, "About.ThirdPartyNoticesMissingFormat"));
    }

    public string ProductName { get; }
    public string CurrentVersionDisplay { get; }
    public string Maintainer { get; }
    public string Organization { get; }
    public string CopyrightNotice { get; }
    public string RepositoryAddress { get; }
    public string LicenseDisplay { get; }
    public string LogsDirectory { get; }
    public string CopyrightNoticeFilePath { get; }
    public string ThirdPartyNoticesFilePath { get; }

    public ICommand OpenRepositoryCommand { get; }
    public ICommand OpenLogsDirectoryCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand OpenCopyrightNoticeCommand { get; }
    public ICommand OpenThirdPartyNoticesCommand { get; }

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

    private void OpenPathOrUrl(string pathOrUrl)
    {
        if (_externalTargetOpener.TryOpen(pathOrUrl, out var errorMessage))
            return;

        ToastService.Instance.Error(string.Format(L("About.OpenTargetFailedFormat"), errorMessage ?? L("Settings.UnknownError")));
    }

    private void OpenBundledDocument(string path, string missingMessageKey)
    {
        try
        {
            if (!File.Exists(path))
            {
                ToastService.Instance.Error(string.Format(L(missingMessageKey), path));
                return;
            }

            OpenPathOrUrl(path);
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
