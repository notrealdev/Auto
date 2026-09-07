namespace Auto.UI.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

public static class NumericInputBehavior {
	public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
		"IsEnabled", typeof(bool), typeof(NumericInputBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

	public static bool GetIsEnabled(TextBox target) => (bool)target.GetValue(IsEnabledProperty);

	public static void SetIsEnabled(TextBox target, bool value) => target.SetValue(IsEnabledProperty, value);

	private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
		if (d is not TextBox textBox) return;

		if ((bool)e.NewValue) {
			textBox.PreviewTextInput += OnPreviewTextInput;
			DataObject.AddPastingHandler(textBox, OnPaste);
		} else {
			textBox.PreviewTextInput -= OnPreviewTextInput;
			DataObject.RemovePastingHandler(textBox, OnPaste);
		}
	}

	private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e) {
		e.Handled = !e.Text.All(char.IsDigit);
	}

	private static void OnPaste(object sender, DataObjectPastingEventArgs e) {
		if (e.DataObject.GetData(DataFormats.Text) is not string text || !text.All(char.IsDigit)) e.CancelCommand();
	}
}
