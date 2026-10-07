using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.GitHub;
using Windows.ApplicationModel.DataTransfer;

namespace Tasklabe.App.ViewModels;

public enum SignInStage
{
    Welcome,
    WaitingForBrowser,
    SettingUp,
}

/// <summary>サインインと初回セットアップ（要件 F-AUTH-01、F-SETUP-01、02）。</summary>
public sealed partial class SignInViewModel(AppServices services) : ObservableObject
{
    private CancellationTokenSource? _cts;
    private DeviceCode? _code;

    public event EventHandler? Completed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsWaiting), nameof(IsSettingUp))]
    public partial SignInStage Stage { get; set; } = SignInStage.Welcome;

    [ObservableProperty]
    public partial string UserCode { get; set; } = "";

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }

    [ObservableProperty]
    public partial string RepositoryName { get; set; } = ProjectConventions.DefaultPersonalRepositoryName;

    /// <summary>サインイン画面を表示した理由（認証の失効など）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo))]
    public partial string? InfoText { get; set; }

    public bool HasInfo => !string.IsNullOrEmpty(InfoText);

    public bool IsWelcome => Stage == SignInStage.Welcome;

    public bool IsWaiting => Stage == SignInStage.WaitingForBrowser;

    public bool IsSettingUp => Stage == SignInStage.SettingUp;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [RelayCommand]
    private async Task SignInAsync()
    {
        ErrorText = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var flow = new DeviceFlow(services.Http, services.Host);
            _code = await flow.RequestCodeAsync(ct);
            UserCode = _code.UserCode;
            Stage = SignInStage.WaitingForBrowser;
            CopyCodeAndOpenBrowser();

            var token = await flow.WaitForTokenAsync(_code, ct);
            services.SetToken(token);

            Stage = SignInStage.SettingUp;
            ProgressText = "アカウントを確認しています";
            var user = await services.Api.GetViewerAsync(ct);
            services.Tokens.Save(user.Login, token);

            // 前回と別のアカウントでサインインした場合は、前のアカウントのキャッシュを消す
            if (services.CurrentSettings.UserId is { } previous && previous != user.Id)
            {
                await services.Store.ClearAsync(ct);
            }

            var repositoryName = string.IsNullOrWhiteSpace(RepositoryName) ? ProjectConventions.DefaultPersonalRepositoryName : RepositoryName.Trim();
            var repository = await services.Workspace.EnsurePersonalWorkspaceAsync(
                user, repositoryName, new Progress<string>(text => ProgressText = text), ct);

            var settings = services.CurrentSettings;
            settings.UserId = user.Id;
            settings.UserLogin = user.Login;
            settings.UserName = user.Name;
            settings.PersonalRepositoryId = repository.Id;
            settings.PersonalRepositoryName = repository.NameWithOwner;
            services.SaveSettings();

            ProgressText = "タスクを取得しています";
            await services.Sync.SyncAsync(force: true, ct);

            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Stage = SignInStage.Welcome;
        }
        catch (GitHubException ex)
        {
            ErrorText = ex.Message;
            Stage = SignInStage.Welcome;
        }
        catch (Exception ex)
        {
            AppLog.Error("SignIn", ex);
            ErrorText = $"予期しないエラーが発生しました。詳細はログを確認してください（{AppLog.Path}）。";
            Stage = SignInStage.Welcome;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CopyCodeAndOpenBrowser()
    {
        if (_code is null)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(_code.UserCode);
        Clipboard.SetContent(package);
        Browser.Open(_code.VerificationUri);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
