namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using Auto.Runtime;


// Một dòng trong danh sách điểm train. Chỉ hiển thị, không cho sửa tay toạ độ — muốn điểm mới thì đứng tại chỗ
// đó rồi bấm "Thêm điểm hiện tại", đúng cách AutoFS làm (nút "Thêm" ở Settings_TọaĐộTrain).
public sealed class TrainingPointRowViewModel(TrainingPoint point) : ViewModelBase {
	// Cùng thang quy đổi với AttackViewModel.RawXScale/RawYScale và StreamTree.XYDisplay của AutoFS.
	private const int RawXScale = 256;
	private const int RawYScale = 512;

	public TrainingPoint Point { get; } = point;

	public string Coordinate => $"{Point.RawX / RawXScale}/{Point.RawY / RawYScale}";

	// Chỉ tên map, không kèm số id (chủ dự án chốt 2026-09-22). Vẫn lùi về "Map <id>" khi bảng tên không có map
	// đó, không thì dòng hiện ra trống trơn và không biết là bãi nào.
	//
	// Dùng lại TrainingPoint.MapName thay vì tra bảng lần thứ hai: một nguồn tra tên duy nhất cho cả giao diện và
	// file JSON, không thể lệch nhau.
	public string MapName => Point.MapName.Length > 0 ? Point.MapName : $"Map {Point.MapId}";

	// Quái của bãi. Điểm lưu trước lượt 2026-09-22 không có quái nên để trống — không bịa chữ "Tất cả" vào đó, vì
	// lúc áp dụng điểm cũ thì quái đang chọn được giữ nguyên chứ không bị ép về "Tất cả".
	public string MonsterName => Point.HasMonster ? Point.MonsterName : "";
}

// Hộp thoại quản lý điểm train, mở từ nút bánh răng ở dòng "Quanh điểm" (tab Đánh).
//
// Khuôn lấy nguyên PotionNameDialogViewModel: dữ liệu được sao chép vào đây, chỉ ghi ngược khi người dùng bấm
// "Đồng ý"; đóng bằng cách bắn CloseRequested để code-behind đặt DialogResult.
//
// AutoFS đặt màn này là một TRANG điều hướng (Settings_TọaĐộTrain.xaml) chứ không phải hộp thoại. Auto không có
// frame điều hướng nên dùng ShowDialog — lệch có chủ ý, không phải sai sót.
public sealed class TrainingPointDialogViewModel : ViewModelBase {
	// Đọc vị trí + map hiện tại của client. Truyền vào từ AttackViewModel để hộp thoại không phải biết gì về
	// GameWindow, và để chỉ có MỘT đường đọc map dùng chung với đường đặt tâm bãi.
	private readonly Func<(bool Success, TrainingPoint Point, string Failure)> readCurrentPoint;
	private TrainingPointRowViewModel? selectedPoint;

	public TrainingPointDialogViewModel(IEnumerable<TrainingPoint> points, int activeMapId,
		Func<(bool Success, TrainingPoint Point, string Failure)> readCurrentPoint) {
		this.readCurrentPoint = readCurrentPoint;
		foreach (TrainingPoint point in points) Points.Add(new TrainingPointRowViewModel(point));
		selectedPoint = Points.FirstOrDefault(row => row.Point.MapId == activeMapId);

		AddCurrentCommand = new RelayCommand(_ => AddCurrent());
		DeleteCommand = new RelayCommand(_ => DeleteSelected(), _ => selectedPoint != null);
		AcceptCommand = new RelayCommand(_ => CloseRequested?.Invoke(this, true));
		CancelCommand = new RelayCommand(_ => CloseRequested?.Invoke(this, false));
		// Double-click = ÁP DỤNG điểm đang trỏ, tức đóng hộp thoại như bấm "Lưu" (chủ dự án chốt 2026-09-22).
		//
		// LỆCH CÓ CHỦ Ý so với AutoFS: bên đó double-click là XOÁ (Settings_TọaĐộTrain -> DeleteTọaĐộ). Nhưng thao
		// tác hay dùng nhất là chọn một bãi đã lưu để áp dụng cho account khác, nên gán double-click cho việc đó;
		// xoá vẫn còn nút "Xoá" ngay bên cạnh. Giữ double-click là xoá còn nguy hiểm hơn: nhỡ tay là mất điểm đã lưu.
		ApplySelectedCommand = new RelayCommand(_ => CloseRequested?.Invoke(this, true), _ => selectedPoint != null);
	}

	public ObservableCollection<TrainingPointRowViewModel> Points { get; } = [];

	public TrainingPointRowViewModel? SelectedPoint {
		get => selectedPoint;
		set => SetField(ref selectedPoint, value);
	}

	public RelayCommand AddCurrentCommand { get; }

	public RelayCommand DeleteCommand { get; }

	public RelayCommand AcceptCommand { get; }

	public RelayCommand ApplySelectedCommand { get; }

	public RelayCommand CancelCommand { get; }

	public event EventHandler<bool>? CloseRequested;

	// Không có dòng thông báo nào dưới danh sách (chủ dự án chốt 2026-09-22): thêm/xoá đã thấy ngay trên danh
	// sách rồi. Ca duy nhất không tự lộ ra là không đọc được vị trí — ghi ra log cho account đó thay vì chiếm
	// một dòng cố định trên hộp thoại.
	private void AddCurrent() {
		(bool success, TrainingPoint point, string failure) = readCurrentPoint();
		if (! success) {
			DebugLog.AddProfileEvent($"TRAINING_POINT_ADD_HỎNG | {failure}");
			return;
		}
		// Chống trùng theo cặp map + toạ độ HIỂN THỊ, đúng khoá so trùng của AutoFS (EmulatorHelper.cs:436-469
		// lọc bằng IdMap && XYDisplay). So theo toạ độ raw sẽ coi hai lần đứng lệch vài raw là hai điểm khác nhau.
		TrainingPointRowViewModel candidate = new(point);
		TrainingPointRowViewModel? duplicate = Points.FirstOrDefault(row =>
			row.Point.MapId == point.MapId && row.Coordinate == candidate.Coordinate);
		if (duplicate != null) {
			SelectedPoint = duplicate;
			return;
		}
		Points.Add(candidate);
		SelectedPoint = candidate;
	}

	private void DeleteSelected() {
		if (selectedPoint == null) return;
		int index = Points.IndexOf(selectedPoint);
		Points.Remove(selectedPoint);
		SelectedPoint = Points.Count == 0 ? null : Points[Math.Clamp(index, 0, Points.Count - 1)];
	}
}
