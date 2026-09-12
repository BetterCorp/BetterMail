using System.Globalization;
using Avalonia.Data.Converters;
using BetterMail.Core;

namespace BetterMail.App;

public sealed class MailSearchLocationConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count >= 2 && values[0] is MailMessage message && values[1] is MainWindowViewModel viewModel
            ? viewModel.MailLocation(message) : "";
}
