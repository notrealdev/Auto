namespace Auto.UI.ViewModels;

using Auto.Runtime;

public sealed class AccountRowViewModel(GameWindow gameWindow) : ViewModelBase {
	public GameWindow GameWindow { get; } = gameWindow;

	public bool AutoEnabled {
		get => GameWindow.Enabled;
		set {
			if (GameWindow.Enabled == value) return;
			GameWindow.Enabled = value;
			OnPropertyChanged();
		}
	}

	public bool IsIdentified => !string.IsNullOrWhiteSpace(GameWindow.CharacterName);

	public string DisplayName => IsIdentified
		? $"{GameWindow.CharacterName} ({(GameWindow.Level > 0 ? GameWindow.Level.ToString() : "?")})"
		: "Chưa đăng nhập!";

	public string Vitals => !IsIdentified || GameWindow.HpPercent < 0 || GameWindow.MpPercent < 0
		? "-/-"
		: $"{GameWindow.HpPercent}% | {GameWindow.MpPercent}%";

	public void Refresh() {
		OnPropertyChanged(nameof(IsIdentified));
		OnPropertyChanged(nameof(DisplayName));
		OnPropertyChanged(nameof(Vitals));
		OnPropertyChanged(nameof(AutoEnabled));
	}
}
