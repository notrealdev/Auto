namespace Auto.Runtime;

using System.Collections.Concurrent;
using System.Reflection;

// Chép giá trị giữa hai đối tượng Settings CÙNG KIỂU, từng trường một.
//
// Vì sao phải chép chứ không gán đè: GameWindow truyền các đối tượng Settings BẰNG THAM CHIẾU vào engine ngay
// trong constructor (GameWindow.cs: AttackEngine = new Engine(AttackSettings, ...), LowHpEngine(BasicSettings,...),
// ChatEngine(MarketSettings,...)...). Thay đối tượng thì engine vẫn giữ tham chiếu cũ và cấu hình nạp vào không có
// tác dụng — mà cũng không gán được, các property đó là get-only.
//
// Vì sao reflection chứ không viết CopyFrom tay cho từng lớp: sáu lớp settings cộng lại hơn 90 property và vẫn
// đang được thêm liên tục (Attack/Settings.cs có trường thêm mới 2026-09-11, 09-12, 09-17). CopyFrom viết tay
// nghĩa là thêm một setting mới mà quên sửa thêm một file nữa thì setting đó âm thầm không được lưu, build vẫn
// xanh, và lỗi chỉ lộ ra sau khi đã mất cấu hình thật.
//
// Chi phí: 6 đối tượng x 6 client = 36 lượt chép mỗi 30 giây, với PropertyInfo[] đã cache — không nằm trên đường
// nóng 100ms của AccountEngineCoordinator.TickOne.
internal static class SettingsCopier {
	private static readonly ConcurrentDictionary<Type, PropertyInfo[]> scalarCache = new();
	private static readonly ConcurrentDictionary<Type, string[]> skippedCache = new();

	// Chép mọi property đọc-ghi được thuộc kiểu ĐƠN GIẢN. Kiểu phức tạp (Dictionary, List...) cố ý KHÔNG chép ở
	// đây — nơi gọi phải tự xử lý, xem AccountProfileStore.ApplyLootDictionaries.
	public static void CopyScalars(object source, object target) {
		if (source.GetType() != target.GetType()) {
			throw new ArgumentException($"Hai đối tượng khác kiểu: {source.GetType().Name} và {target.GetType().Name}.");
		}
		foreach (PropertyInfo property in GetScalarProperties(source.GetType())) {
			property.SetValue(target, property.GetValue(source));
		}
	}

	// Tên các property BỊ BỎ QUA vì không phải kiểu đơn giản.
	//
	// Lưới an toàn của phép chép bằng reflection: AccountProfileStore in danh sách này ra log một lần lúc nạp. Ai
	// thêm một property kiểu phức tạp mới vào bất kỳ lớp settings nào thì dòng log đó đổi nội dung ngay và người
	// đọc thấy liền, thay vì mất dữ liệu trong im lặng.
	public static IReadOnlyList<string> DescribeSkipped(Type type) => skippedCache.GetOrAdd(type, BuildSkipped);

	private static PropertyInfo[] GetScalarProperties(Type type) => scalarCache.GetOrAdd(type, BuildScalars);

	private static PropertyInfo[] BuildScalars(Type type) => type
		.GetProperties(BindingFlags.Public | BindingFlags.Instance)
		.Where(property => property.CanRead && property.CanWrite && IsSimple(property.PropertyType))
		.ToArray();

	private static string[] BuildSkipped(Type type) => type
		.GetProperties(BindingFlags.Public | BindingFlags.Instance)
		.Where(property => property.CanRead && ! (property.CanWrite && IsSimple(property.PropertyType)))
		.Select(property => property.Name)
		.ToArray();

	private static bool IsSimple(Type type) {
		Type actual = Nullable.GetUnderlyingType(type) ?? type;
		return actual.IsPrimitive || actual.IsEnum || actual == typeof(string) || actual == typeof(decimal) || actual == typeof(DateTime);
	}
}
