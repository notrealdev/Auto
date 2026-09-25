namespace Auto.Login;

// Khoá độc quyền cho MỌI luồng đăng nhập, dù là tab Login chạy tay hay luồng tự đăng nhập lại.
//
// Vì sao bắt buộc: LoginAutomation mở Game.exe rồi bơm chuỗi lệnh 280/281/282 vào cửa sổ vừa hiện ra. Hai luồng
// chạy song song sẽ mở hai client cùng lúc và mỗi luồng không có cách nào biết cửa sổ nào là của mình — chuỗi
// lệnh bắn lẫn sang nhau. Một lượt đăng nhập chặn 60-90 giây nên cửa sổ đua này rất rộng.
internal static class LoginSession {
	private static readonly object SyncRoot = new();
	private static string? currentOwner;

	public static bool IsBusy {
		get { lock (SyncRoot) return currentOwner != null; }
	}

	// Mô tả ai đang giữ khoá, để dòng log nói rõ vì sao bị hoãn thay vì chỉ "đang bận".
	public static string CurrentOwner {
		get { lock (SyncRoot) return currentOwner ?? ""; }
	}

	// Trả về null khi đang có luồng khác giữ khoá. Dùng với using để tự nhả kể cả khi ném exception.
	public static IDisposable? TryEnter(string owner) {
		lock (SyncRoot) {
			if (currentOwner != null) return null;
			currentOwner = owner;
		}
		return new Lease();
	}

	private static void Release() {
		lock (SyncRoot) currentOwner = null;
	}

	private sealed class Lease : IDisposable {
		private bool released;

		public void Dispose() {
			if (released) return;
			released = true;
			Release();
		}
	}
}
