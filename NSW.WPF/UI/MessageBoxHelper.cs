using System.Windows;
using Res = NSW.Core.Properties.Resources;

namespace NSW.WPF.UI;

public class MessageBoxHelper
{
    public static Action<string, string>? NotificationHandler { get; set; }
    public static Func<string, bool>? QuestionHandler { get; set; }

    public static void ShowInfo(string msg)
    {
        if (NotificationHandler is not null) { NotificationHandler("info", msg); return; }
        MessageBox.Show(msg, Res.Msg_Title_Info, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void ShowWarning(string msg)
    {
        if (NotificationHandler is not null) { NotificationHandler("warning", msg); return; }
        MessageBox.Show(msg, Res.Msg_Title_Warning, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static void ShowError(string msg)
    {
        if (NotificationHandler is not null) { NotificationHandler("error", msg); return; }
        MessageBox.Show(msg, Res.Msg_Title_Error, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static bool ShowQuestion(string msg)
    {
        if (QuestionHandler is not null)
            return QuestionHandler(msg);
        return MessageBox.Show(msg, Res.Msg_Title_Question, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
}
