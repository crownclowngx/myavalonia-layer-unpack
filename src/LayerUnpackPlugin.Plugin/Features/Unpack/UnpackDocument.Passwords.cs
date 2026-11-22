using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;

namespace LayerUnpackPlugin.Features.Unpack;

public sealed partial class UnpackDocument
{
    private static readonly SavedPassword NoSavedPassword = new(Guid.Empty, "不使用常用密码", false);
    private readonly ISavedPasswordStore? _passwordStore;
    private Guid? _editingPasswordId;

    [ObservableProperty] private IReadOnlyList<SavedPassword> _passwordChoices = [NoSavedPassword];
    [ObservableProperty] private SavedPassword? _selectedSavedPassword = NoSavedPassword;
    [ObservableProperty] private bool _isPasswordManagerOpen;
    [ObservableProperty] private string _passwordHint = "";
    [ObservableProperty][property: JsonIgnore] private string _passwordDraft = "";
    [ObservableProperty] private bool _makeDefaultPassword;
    [ObservableProperty] private string _passwordStoreMessage = "";

    public bool HasPasswordStore => _passwordStore?.IsAvailable == true;
    public IEnumerable<SavedPassword> ManagedPasswords => PasswordChoices.Where(password => password.Id != Guid.Empty);
    public bool EditingSavedPassword => _editingPasswordId is not null;
    public string PasswordSaveLabel => EditingSavedPassword ? "保存修改" : "保存密码";

    partial void OnPasswordChoicesChanged(IReadOnlyList<SavedPassword> value) => OnPropertyChanged(nameof(ManagedPasswords));

    private void RefreshPasswords(bool selectDefault = false, Guid? preferredId = null)
    {
        if (!HasPasswordStore) return;
        try
        {
            var priorId = preferredId ?? SelectedSavedPassword?.Id;
            var existing = _passwordStore!.List();
            PasswordChoices = [NoSavedPassword, .. existing];
            SelectedSavedPassword = (selectDefault ? existing.FirstOrDefault(password => password.IsDefault)
                : existing.FirstOrDefault(password => password.Id == priorId)) ?? NoSavedPassword;
        }
        catch (Exception)
        {
            PasswordChoices = [NoSavedPassword];
            SelectedSavedPassword = NoSavedPassword;
            PasswordStoreMessage = "暂时无法读取常用密码，可继续使用临时密码。";
        }
    }

    private string[] ResolveBatchPasswords()
    {
        var manual = PasswordPool.ParseLines(PasswordText);
        if (SelectedSavedPassword is not { Id: var id } || id == Guid.Empty || !HasPasswordStore) return manual;
        string saved;
        try { saved = _passwordStore!.ReadPassword(id); }
        catch (Exception) { throw new UnpackValidationException("Passwords", "所选常用密码不可用，请重新选择，或使用临时密码。"); }
        return PasswordPool.Normalize(new[] { saved }.Concat(manual));
    }

    [RelayCommand]
    private void TogglePasswordManager()
    {
        if (!CanEdit || !HasPasswordStore) return;
        if (IsPasswordManagerOpen) { ClosePasswordManager(); return; }
        PasswordStoreMessage = "";
        RefreshPasswords();
        IsPasswordManagerOpen = true;
    }

    [RelayCommand]
    private void EditSavedPassword(SavedPassword? password)
    {
        if (!CanEdit || !HasPasswordStore || password is null) return;
        _editingPasswordId = password.Id;
        PasswordHint = password.Hint;
        PasswordDraft = "";
        MakeDefaultPassword = password.IsDefault;
        PasswordStoreMessage = "密码留空可保留原密码。";
        OnPropertyChanged(nameof(EditingSavedPassword));
        OnPropertyChanged(nameof(PasswordSaveLabel));
    }

    [RelayCommand]
    private void SavePassword()
    {
        if (!CanEdit || !HasPasswordStore) return;
        try
        {
            var makeDefault = MakeDefaultPassword;
            var id = _passwordStore!.Save(_editingPasswordId, PasswordHint, PasswordDraft, makeDefault);
            ResetPasswordEditor();
            PasswordStoreMessage = "已保存常用密码。";
            RefreshPasswords(preferredId: makeDefault ? id : null);
        }
        catch (ArgumentException e) { PasswordStoreMessage = e.Message; }
        catch (Exception) { PasswordStoreMessage = "保存未完成，请检查密码库是否可用后重试。"; }
    }

    [RelayCommand]
    private void RemoveSavedPassword(SavedPassword? password)
    {
        if (!CanEdit || !HasPasswordStore || password is null) return;
        try
        {
            _passwordStore!.Remove(password.Id);
            if (_editingPasswordId == password.Id) ResetPasswordEditor();
            PasswordStoreMessage = "已移除常用密码。";
            RefreshPasswords();
        }
        catch (Exception) { PasswordStoreMessage = "未能移除常用密码，请重试。"; }
    }

    [RelayCommand]
    private void ResetPasswordEditor()
    {
        _editingPasswordId = null;
        PasswordDraft = "";
        PasswordHint = "";
        MakeDefaultPassword = false;
        PasswordStoreMessage = "";
        OnPropertyChanged(nameof(EditingSavedPassword));
        OnPropertyChanged(nameof(PasswordSaveLabel));
    }

    private void ClosePasswordManager()
    {
        IsPasswordManagerOpen = false;
        ResetPasswordEditor();
    }
}
