namespace Tasklabe.App.Services;

/// <summary>
/// 画面に書いておく操作のヒント（ダイアログやピッカーの下端のキーの案内など）を出すか（要件 F-SET-07）。
/// ツールチップと読み上げの名前は、求めたときにだけ出るため、この設定によらず出す。
/// </summary>
public static class Hints
{
    public static bool Shown => App.Current.Services.CurrentSettings.ShowHints;
}
