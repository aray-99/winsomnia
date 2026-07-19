using System.Globalization;

namespace Winsomnia.Desktop;

public static class Localization
{
    private static bool Japanese(CultureInfo culture) => culture.TwoLetterISOLanguageName == "ja";

    private static readonly IReadOnlyDictionary<string, (string English, string Japanese)> Values =
        new Dictionary<string, (string, string)>
        {
            ["AppTitle"] = ("winsomnia settings", "winsomnia 設定"),
            ["Home"] = ("Home", "ホーム"),
            ["Status"] = ("Status", "状態"),
            ["Paused"] = ("Paused", "一時停止"),
            ["Armed"] = ("Armed", "有効化状態"),
            ["Authorization"] = ("Lock authorization", "ロック認可"),
            ["Reason"] = ("Authorization reason", "認可理由"),
            ["Yes"] = ("Yes", "はい"),
            ["No"] = ("No", "いいえ"),
            ["PauseSucceeded"] = ("winsomnia is paused and locking is disarmed.", "winsomnia を一時停止し、ロックを無効化しました。"),
            ["PauseFailed"] = ("Pause failed; verify the authorization status.", "一時停止に失敗しました。認可状態を確認してください。"),
            ["Schedule"] = ("Daily schedule", "毎日のスケジュール"),
            ["Strength"] = ("Restriction strength", "制限の強さ"),
            ["Exception"] = ("Planned exception", "例外日の予約"),
            ["Diagnostics"] = ("Diagnostics", "診断"),
            ["Start"] = ("Start (HH:mm)", "開始 (HH:mm)"),
            ["End"] = ("End (HH:mm)", "終了 (HH:mm)"),
            ["Stage"] = ("Apply after 24 hours", "24時間後の変更を予約"),
            ["Pending"] = ("Pending change", "保留中の変更"),
            ["Credit"] = ("Unlock credit", "一時解除クレジット"),
            ["Strict"] = ("Strict: +5/day, max 30", "強め: 1日5分、上限30分"),
            ["Standard"] = ("Standard: +10/day, max 60", "標準: 1日10分、上限60分"),
            ["Flexible"] = ("Flexible: +15/day, max 120", "柔軟: 1日15分、上限120分"),
            ["Reserve"] = ("Reserve at least 24 hours ahead", "24時間以上前に予約"),
            ["Refresh"] = ("Refresh", "更新"),
            ["Unavailable"] = ("Engine unavailable. winsomnia remains safely paused.", "Engineに接続できません。winsomniaは安全停止のままです。"),
            ["RestrictionPrompt"] = ("Restricted time", "制限時間です"),
            ["RelockMessage"] = ("The workstation will lock when the countdown ends.", "カウントダウン終了後に画面をロックします。"),
            ["LockNow"] = ("Return to lock", "今すぐロックへ戻る"),
            ["Spend"] = ("Spend credit", "クレジットを使う"),
            ["ReadOnly"] = ("This status panel has no controls.", "この状態パネルから設定や停止はできません。")
        };

    public static IEnumerable<string> Keys => Values.Keys;

    public static string Text(string key, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        return Japanese(culture) ? Values[key].Japanese : Values[key].English;
    }
}
