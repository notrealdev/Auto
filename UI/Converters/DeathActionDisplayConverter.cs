namespace Auto.UI.Converters;

using System.Globalization;
using System.Windows.Data;
using Auto.Runtime;

public sealed class DeathActionDisplayConverter : IValueConverter {
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
		return value is DeathAction action ? DeathActionNames.ToDisplay(action) : "";
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
		throw new NotSupportedException();
	}
}
