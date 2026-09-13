using System.Globalization;
using Avalonia.Data.Converters;
using BetterMail.Core;

namespace BetterMail.App;

public sealed class MailActionFeedbackConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var message = values.FirstOrDefault() switch { MailMessage mail => mail, ConversationMessageItem item => item.Message, _ => null };
        var pending = values.ElementAtOrDefault(1) is MainWindowViewModel owner &&
            (values.FirstOrDefault() is LocalDraft draft ? owner.IsDraftDeletionPending(draft) : message is not null && owner.IsMessageActionPending(message));
        return (parameter as string) switch { "enabled" => !pending, "opacity" => pending ? 0.6d : 1d, _ => (object)pending };
    }
}
