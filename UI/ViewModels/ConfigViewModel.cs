namespace Auto.UI.ViewModels;

using System.Windows;

// Tab "Config": nơi duy nhất ghi/nạp Profiles.json bằng tay.
//
// Tách khỏi khung chính (chủ dự án chốt 2026-09-23): hai nút này trước nằm ngay dưới danh sách account, dính vào
// phần khung dùng chung cho mọi tab nên chiếm chỗ cố định và trông như một phần của danh sách account.
//
// KHÔNG dựng lại theo account đang chọn — giống tab Login: hai lệnh này tác động lên TẤT CẢ account cùng lúc,
// không phải lên riêng account đang trỏ.
public sealed class ConfigViewModel : ViewModelBase {
	public ConfigViewModel() : this(null, null) { }

	private string statusText = "";

	public ConfigViewModel(Action? saveProfiles, Action? applyProfiles) {
		SaveProfilesCommand = new RelayCommand(_ => {
			// Hỏi lại trước khi ghi (chủ dự án yêu cầu 2026-09-26): lưu là ghi đè Profiles.json của MỌI account cùng lúc.
			MessageBoxResult answer = MessageBox.Show(
				Application.Current.MainWindow,
				"Lưu cấu hình hiện tại của TẤT CẢ account?\n\nFile cấu hình đã lưu trước đó sẽ bị ghi đè.",
				"Lưu cấu hình",
				MessageBoxButton.YesNo,
				MessageBoxImage.Warning,
				MessageBoxResult.No);
			if (answer != MessageBoxResult.Yes) {
				StatusText = "Đã huỷ lưu cấu hình.";
				return;
			}
			saveProfiles?.Invoke();
			StatusText = "Đã lưu cấu hình.";
		});
		ApplyProfilesCommand = new RelayCommand(_ => {
			applyProfiles?.Invoke();
			StatusText = "Đã áp dụng cấu hình đã lưu.";
		});
	}

	public RelayCommand SaveProfilesCommand { get; }

	public RelayCommand ApplyProfilesCommand { get; }

	public string StatusText {
		get => statusText;
		private set => SetField(ref statusText, value);
	}
}
