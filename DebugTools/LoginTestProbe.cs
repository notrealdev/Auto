namespace Auto.DebugTools;

using Auto.Login;

// Chạy thử luồng đăng nhập với ĐÚNG account đầu tiên trong Login\Login.json, để kiểm chứng chuỗi lệnh
// 280/281/282/283/284 trên client hiện tại trước khi dùng tab Login chạy cả danh sách.
//
// Đây là công cụ chẩn đoán: nó MỞ CLIENT THẬT và GÕ TÀI KHOẢN THẬT, không phải chạy khô.
public static class LoginTestProbe {
	public static string Run() {
		if (! Settings.TryLoad(Settings.DefaultPath, out Settings settings, out string failure)) {
			return $"Không đọc được cấu hình đăng nhập.\r\nĐường dẫn: {Settings.DefaultPath}\r\nLý do: {failure}";
		}

		Settings single = new() {
			ExeLink = settings.ExeLink,
			Speed = settings.Speed,
			DelayLogin = settings.DelayLogin,
			Accounts = [settings.Accounts[0]]
		};

		List<string> lines = [
			$"Cấu hình: {Settings.DefaultPath}",
			$"ExeLink={single.ExeLink} | Speed={single.Speed} | DelayLogin={single.DelayLogin}ms",
			$"Chạy thử account đầu tiên: {single.Accounts[0].User} | PhânVùng={single.Accounts[0].Partition} | MáyChủ={single.Accounts[0].Server}",
			""
		];
		new LoginAutomation().Run(single, lines.Add, CancellationToken.None);
		lines.Add("");
		lines.Add("Chuỗi lệnh đã gửi xong. CHƯA đọc được trạng thái client nên KHÔNG kết luận đăng nhập thành công.");
		lines.Add("Nhìn màn hình đăng nhập của client vừa mở: ô tài khoản và mật khẩu có chữ chạy vào không?");
		return string.Join("\r\n", lines);
	}
}
