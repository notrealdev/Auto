namespace Auto.UI.ViewModels;

using Auto.Runtime;
using Auto.Utils;

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

	// Dòng account theo format chủ dự án chốt 2026-09-24: Tên | Tiền vạn | Sức lực hiện tại/tối đa | HP% | MP%.
	// Cột tên KHÔNG kèm cấp như DisplayName (DisplayName vẫn giữ cho hộp thoại đóng cửa sổ).
	public string CharacterNameText => IsIdentified ? GameWindow.CharacterName : "Chưa đăng nhập!";

	// Số tiền theo vạn (1 vạn = 10.000 xu), chỉ hiện lúc đã đăng nhập, cùng luật với sức lực. Cắt (không làm tròn) còn
	// 1 chữ số sau dấu phẩy, chốt 2026-09-24: 129.999 xu hiện "12,9v", 11.901.000 xu hiện "1190,1v". Tính bằng số nguyên
	// (xu / 1.000 = số phần mười vạn) để không dính sai số của số thực. Ô nhớ đã được chủ dự án đối chiếu với game
	// (GameAddresses.Inventory.Money).
	public string MoneyText {
		get {
			if (! IsIdentified || GameWindow.Money < 0) return "";
			int tenths = GameWindow.Money / (InventoryMoneyReader.CoinsPerTenThousand / 10);
			return $"{tenths / 10},{tenths % 10}v";
		}
	}

	public string StrengthText => IsIdentified && GameWindow.StrengthCurrent >= 0 && GameWindow.StrengthMaximum > 0
		? $"{GameWindow.StrengthCurrent}/{GameWindow.StrengthMaximum}"
		: "";

	// Quá tải: sức lực hiện tại vượt tối đa thì game khoá di chuyển. Cả dòng account in đỏ để nhìn ra ngay.
	public bool IsOverweight => IsIdentified && GameWindow.StrengthMaximum > 0 && GameWindow.StrengthCurrent > GameWindow.StrengthMaximum;

	public void Refresh() {
		OnPropertyChanged(nameof(IsIdentified));
		OnPropertyChanged(nameof(DisplayName));
		OnPropertyChanged(nameof(CharacterNameText));
		OnPropertyChanged(nameof(MoneyText));
		OnPropertyChanged(nameof(StrengthText));
		OnPropertyChanged(nameof(IsOverweight));
		OnPropertyChanged(nameof(Vitals));
		OnPropertyChanged(nameof(AutoEnabled));
	}
}
