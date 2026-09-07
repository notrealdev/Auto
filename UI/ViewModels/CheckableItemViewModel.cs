namespace Auto.UI.ViewModels;

public sealed class CheckableItemViewModel(string name, bool isChecked, bool isBuiltIn, Action<bool>? onCheckedChanged = null) : ViewModelBase {
	private bool isChecked = isChecked;

	public string Name { get; } = name;

	public bool IsBuiltIn { get; } = isBuiltIn;

	public bool IsChecked {
		get => isChecked;
		set {
			if (!SetField(ref isChecked, value)) return;
			onCheckedChanged?.Invoke(value);
		}
	}
}
