namespace Auto.UI.Converters;

using System.Globalization;
using System.Windows.Data;
using Auto.Runtime;

public sealed class DeathActionDisplayConverter : IValueConverter {
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
		return value switch {
			DeathAction.StayStill => "Nằm im",
			DeathAction.ReturnToTown => "Về thành",
			DeathAction.Substitute => "Thế mạng",
			DeathAction.Revive => "Phục sinh",
			_ => ""
		};
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
		throw new NotSupportedException();
	}
}
