using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Settings;

namespace Tasklabe.App.Services;

/// <summary>
/// プロジェクトで使う設定（要件 F-SET-01、02）。全体の設定にある個人・チームのプロジェクトの既定に、
/// プロジェクトごとの上書き（GitHub の Project の README に保存）を重ねて決める。
/// 画面はプロジェクトを表示するときに <see cref="Apply"/> を呼び、工数の表示・稼働日をそのプロジェクトのものにする。
/// </summary>
public static class ProjectPreferences
{
    private static Data.AppSettings Settings => App.Current.Services.CurrentSettings;

    /// <summary>いま表示しているプロジェクトの設定（プロジェクトをまたぐ画面では個人のプロジェクトの既定）。</summary>
    public static ResolvedSettings Current { get; private set; } = DefaultSettings.Personal.Resolved;

    /// <summary>個人（team = false）またはチーム（team = true）のプロジェクトの既定。</summary>
    public static DefaultSettings Defaults(bool team) =>
        DefaultSettings.Parse(team ? Settings.TeamDefaults : Settings.PersonalDefaults, Legacy(team));

    public static void SaveDefaults(bool team, DefaultSettings defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (team)
        {
            Settings.TeamDefaults = defaults.ToJson();
        }
        else
        {
            Settings.PersonalDefaults = defaults.ToJson();
        }

        App.Current.Services.SaveSettings();
    }

    /// <summary>プロジェクトで使う設定。null ならプロジェクトをまたぐ画面の設定（個人のプロジェクトの既定）。</summary>
    public static ResolvedSettings Resolve(Project? project)
    {
        if (project is null)
        {
            return Defaults(team: false).Resolved;
        }

        var overrides = project.Settings;

        // 以前この PC にだけ保存していたチームの稼働日は、プロジェクトに設定がなければ引き続き使う
        if (overrides.Calendar is null && Settings.WorkCalendars.GetValueOrDefault(project.Id) is { } legacy)
        {
            overrides = overrides with { Calendar = WorkCalendarRules.Parse(legacy) };
        }

        var resolved = overrides.Over(Defaults(project.IsTeam));

        // 個人のプロジェクトは課題を扱わない（要件 F-UI-PR-01）
        return project.IsTeam ? resolved : resolved with { NewTaskKind = TaskKind.Task };
    }

    /// <summary>そのプロジェクトの設定を、いま使う設定にする（プロジェクトを表示するときに呼ぶ）。</summary>
    public static void Apply(Project? project)
    {
        Current = Resolve(project);
        WorkCalendar.Current = Current.Calendar;
    }

    /// <summary>プロジェクトの設定を保存した。この PC にだけ残っていた古い稼働日の設定は捨てる。</summary>
    public static void ForgetLegacy(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (Settings.WorkCalendars.Remove(project.Id))
        {
            App.Current.Services.SaveSettings();
        }
    }

    /// <summary>既定をまだ保存していないときは、以前の全体の設定（工数の単位、個人の稼働日）から作る。</summary>
    private static DefaultSettings Legacy(bool team)
    {
        var standard = team ? DefaultSettings.Team : DefaultSettings.Personal;
        return standard with
        {
            EffortUnit = Settings.EffortInDays ? EffortUnit.Days : EffortUnit.Hours,
            HoursPerDay = Settings.HoursPerDay is > 0 and <= 24 ? Settings.HoursPerDay : 8,
            Calendar = team ? standard.Calendar : WorkCalendarRules.Parse(Settings.WorkCalendars.GetValueOrDefault("personal")),
        };
    }
}
