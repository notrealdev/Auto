namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;

public sealed class PotionNameDialogViewModel(ObservableCollection<CheckableItemViewModel> potionNames, int quantityLimit) : ViewModelBase {
	private int quantityLimit = Math.Clamp(quantityLimit, 0, 999);

	public ObservableCollection<CheckableItemViewModel> PotionNames { get; } = potionNames;

	public int QuantityLimit {
		get => quantityLimit;
		set => SetField(ref quantityLimit, Math.Clamp(value, 0, 999));
	}

	public event EventHandler<bool>? CloseRequested;

	public RelayCommand AcceptCommand => new(_ => CloseRequested?.Invoke(this, true));

	public RelayCommand CancelCommand => new(_ => CloseRequested?.Invoke(this, false));
}
