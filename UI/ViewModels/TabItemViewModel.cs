namespace Auto.UI.ViewModels;

public sealed class TabItemViewModel(string title, ViewModelBase content) : ViewModelBase {
	private bool isSelected;
	private ViewModelBase content = content;

	public string Title { get; } = title;

	public ViewModelBase Content {
		get => content;
		set => SetField(ref content, value);
	}

	public bool IsSelected {
		get => isSelected;
		set => SetField(ref isSelected, value);
	}
}
