namespace Auto.UI.ViewModels;

public sealed class ClassPriorityDialogViewModel(bool initialTaoist, bool initialSummoner, bool initialWarrior) : ViewModelBase {
	private bool taoist = initialTaoist;
	private bool summoner = initialSummoner;
	private bool warrior = initialWarrior;

	public event EventHandler<bool>? CloseRequested;

	public bool Taoist {
		get => taoist;
		set => SetField(ref taoist, value);
	}

	public bool Summoner {
		get => summoner;
		set => SetField(ref summoner, value);
	}

	public bool Warrior {
		get => warrior;
		set => SetField(ref warrior, value);
	}

	public RelayCommand AcceptCommand => new(_ => CloseRequested?.Invoke(this, true));

	public RelayCommand CancelCommand => new(_ => CloseRequested?.Invoke(this, false));
}
