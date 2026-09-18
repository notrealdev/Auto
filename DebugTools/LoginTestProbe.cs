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

		// Sao chép ĐỦ mọi trường cấu hình, chỉ thu hẹp danh sách account xuống một dòng.
		//
		// Bản cũ chỉ chép ExeLink/Speed/DelayLogin/Accounts, bỏ sót WaitBeforeLogin và StepTimeoutMilliseconds, nên
		// probe chạy bằng GIÁ TRỊ MẶC ĐỊNH chứ không phải cấu hình thật — công cụ chẩn đoán không còn chẩn đoán đúng
		// thứ đang chạy. Hậu quả đo được ngày 2026-09-18: Login.json đặt StepTimeoutMilliseconds=10000 còn mặc định
		// trong Settings.cs là 60000, nên probe chờ 60 giây và chạy trót lọt trong khi bản thật chỉ chờ 10 giây rồi
		// bỏ — 6/6 account hỏng, 4 ca dừng ở lệnh 282 và 2 ca dừng ở lệnh 280, tất cả đều ghi đúng "trong 10000ms"
		// (login.log 12:40:33 - 12:42:33). Chủ dự án báo "debug chạy hoàn hảo mà thực hiện vẫn lỗi" chính là chỗ này.
		Settings single = new() {
			ExeLink = settings.ExeLink,
			Speed = settings.Speed,
			DelayLogin = settings.DelayLogin,
			WaitBeforeLogin = settings.WaitBeforeLogin,
			StepTimeoutMilliseconds = settings.StepTimeoutMilliseconds,
			Accounts = [settings.Accounts[0]]
		};

		List<string> lines = [
			$"Cấu hình: {Settings.DefaultPath}",
			$"ExeLink={single.ExeLink} | Speed={single.Speed} | DelayLogin={single.DelayLogin}ms | ChờTrướcĐăngNhập={single.WaitBeforeLogin}ms | HạnMỗiBước={single.StepTimeoutMilliseconds}ms",
			$"Chạy thử account đầu tiên: {single.Accounts[0].User} | Partition={single.Accounts[0].Partition} | Server={single.Accounts[0].Server}",
			""
		];
		new LoginAutomation().Run(single, lines.Add, CancellationToken.None);
		lines.Add("");
		lines.Add("Chuỗi lệnh đã gửi xong. CHƯA đọc được trạng thái client nên KHÔNG kết luận đăng nhập thành công.");
		lines.Add("Nhìn màn hình đăng nhập của client vừa mở: ô tài khoản và mật khẩu có chữ chạy vào không?");
		return string.Join("\r\n", lines);
	}
}
