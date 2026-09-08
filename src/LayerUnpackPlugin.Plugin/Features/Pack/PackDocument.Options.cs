using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Features.Pack;

/// <summary>按需选项只组装有限的 Headless 参数；规则匹配、分组和名称分配均不在呈现层重复实现。</summary>
public sealed partial class PackDocument
{
    [ObservableProperty] private bool _moreOptionsExpanded;
    [ObservableProperty] private bool _separateArchives;
    [ObservableProperty] private int _compressionIndex;
    [ObservableProperty] private bool _excludeTemporaryFiles;
    [ObservableProperty] private bool _excludeLogs;
    [ObservableProperty] private bool _excludeBuildFolders;
    [ObservableProperty] private bool _excludeGitFolder;
    [ObservableProperty] private bool _excludeNodeModules;
    [ObservableProperty] private bool _encryptionEnabled;
    [ObservableProperty] private bool _showPassword;
    [ObservableProperty]
    [property: JsonIgnore]
    private string _targetPassword = "";
    [ObservableProperty]
    [property: JsonIgnore]
    private string _confirmPassword = "";

    public IReadOnlyList<string> CompressionChoices { get; } = ["标准", "快速", "高压缩", "仅打包"];
    public ObservableCollection<string> EntryMappings { get; } = [];
    public ObservableCollection<string> ExcludedMappings { get; } = [];
    public ObservableCollection<PackGroupDisplay> GroupResults { get; } = [];
    public ObservableCollection<string> PreviousOutputs { get; } = [];
    public char PasswordCharacter => ShowPassword ? '\0' : '●';
    public string ExclusionSummary => $"排除 {_plan?.ExcludedCount ?? 0} 项（命中目录含全部后代），展开可查看原因";

    partial void OnSeparateArchivesChanged(bool value) => InvalidatePlan();
    partial void OnCompressionIndexChanged(int value) => InvalidatePlan();
    partial void OnExcludeTemporaryFilesChanged(bool value) => InvalidatePlan();
    partial void OnExcludeLogsChanged(bool value) => InvalidatePlan();
    partial void OnExcludeBuildFoldersChanged(bool value) => InvalidatePlan();
    partial void OnExcludeGitFolderChanged(bool value) => InvalidatePlan();
    partial void OnExcludeNodeModulesChanged(bool value) => InvalidatePlan();
    partial void OnEncryptionEnabledChanged(bool value)
    {
        if (!value) ClearSecretFields();
        InvalidatePlan();
    }
    partial void OnShowPasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordCharacter));

    private PackBatchRequest CaptureRequest()
    {
        var extensions = new List<string>(); var directories = new List<string>();
        if (ExcludeTemporaryFiles) extensions.AddRange([".tmp", ".bak"]);
        if (ExcludeLogs) extensions.Add(".log");
        if (ExcludeBuildFolders) directories.AddRange(["bin", "obj"]);
        if (ExcludeGitFolder) directories.Add(".git");
        if (ExcludeNodeModules) directories.Add("node_modules");
        return new(Inputs.Select(i => i.Path), OutputDirectory, ArchiveName,
            SeparateArchives ? PackGrouping.Separate : PackGrouping.Combined,
            new() { Compression = (PackCompression)CompressionIndex, Encrypt = EncryptionEnabled, Exclusions = new(extensions, directories) });
    }

    private PackSecret? CaptureSecret()
    {
        if (!EncryptionEnabled) return null;
        if (TargetPassword != ConfirmPassword) throw new PackValidationException("两次目标密码不一致，请重新输入。");
        return new(TargetPassword);
    }
    private void ClearSecretFields() { TargetPassword = ""; ConfirmPassword = ""; ShowPassword = false; }
    private void ResetOptions()
    {
        SeparateArchives = false; CompressionIndex = 0; EncryptionEnabled = false; ClearSecretFields();
        ExcludeTemporaryFiles = false; ExcludeLogs = false; ExcludeBuildFolders = false;
        ExcludeGitFolder = false; ExcludeNodeModules = false; MoreOptionsExpanded = false;
    }
}

public sealed record PackGroupDisplay(string Source, string Status, string Detail, string? OutputPath);
