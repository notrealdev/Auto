namespace Auto.UI.Models;

using System.IO;
using System.Text;

public static class TrainingLocationCatalog {
	public static readonly string[] BeginnerMaps = ["Du Hồn", "Bắc Hải", "Thủ Dương Sơn"];
	public static readonly string[] CityMaps = ["Đồng Quan", "Mạnh Tân", "Mục Dã", "Tam Sơn", "Kỳ Sơn", "Trần Đường", "Tuyệt Long Lĩnh"];

	public static readonly IReadOnlyDictionary<string, string[]> MazeMaps = new Dictionary<string, string[]> {
		["Hoang Mạc"] = ["Hoang Mạc", "Sa Mạc Thổ Thành", "Sa Mạc Phong Than", "Sa Mạc Lục Châu", "Sa Mạc Chết"],
		["Thủy Vực"] = ["Thủy Vực", "Long Cung", "Hải Câu", "Long Vực", "Long Uyên"],
		["Hiên Viên"] = ["Hiên Viên Tầng 1", "Hiên Viên Tầng 2", "Hiên Viên Tầng 3", "Hiên Viên Tầng 4", "Hiên Viên Tầng 5"],
		["Ngọc Tuyền"] = ["Ngọc Tuyền Băng Xuyên", "Băng Xuyên Tuyết Cốc", "Đại Phong Băng Xuyên", "Đại Trạch Băng Xuyên", "Băng Xuyên Chi Cực"]
	};

	private static readonly HashSet<string> AllowedRouteMaps = new(BeginnerMaps.Concat(CityMaps).Concat(MazeMaps.Values.SelectMany(maps => maps)));

	// Ánh xạ tên map theo bảng ResourceStream của AutoFS, port từ D:\G\DEV\UI\AttackPage.cs (ConfirmedTrainingMapIds).
	private static readonly IReadOnlyDictionary<string, int> ConfirmedTrainingMapIds = new Dictionary<string, int> {
		["Bắc Hải"] = 6, ["Mạnh Tân"] = 15, ["Thủ Dương Sơn"] = 10, ["Du Hồn"] = 11, ["Đồng Quan"] = 14, ["Tam Sơn"] = 16, ["Kỳ Sơn"] = 17, ["Mục Dã"] = 18,
		["Hoang Mạc"] = 22, ["Trần Đường"] = 65, ["Sa Mạc Thổ Thành"] = 23, ["Sa Mạc Phong Than"] = 24, ["Sa Mạc Lục Châu"] = 25, ["Sa Mạc Chết"] = 26,
		["Thủy Vực"] = 37, ["Long Cung"] = 38, ["Hải Câu"] = 39, ["Long Vực"] = 40, ["Long Uyên"] = 41,
		["Hiên Viên Tầng 1"] = 27, ["Hiên Viên Tầng 2"] = 28, ["Hiên Viên Tầng 3"] = 29, ["Hiên Viên Tầng 4"] = 30, ["Hiên Viên Tầng 5"] = 31,
		["Ngọc Tuyền Băng Xuyên"] = 32, ["Băng Xuyên Tuyết Cốc"] = 33, ["Đại Phong Băng Xuyên"] = 34, ["Đại Trạch Băng Xuyên"] = 35, ["Băng Xuyên Chi Cực"] = 36,
		["Tuyệt Long Lĩnh"] = 19
	};

	// Trả về map id đã xác nhận của tên map; 0 khi không có trong bảng để nơi gọi tự quyết định cách xử lý.
	public static int GetMapId(string mapName) => ConfirmedTrainingMapIds.GetValueOrDefault(mapName);

	private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, CoordinateOption[]>>? locations;

	public static string[] GetMonsters(string map) {
		return GetLocations().TryGetValue(map, out IReadOnlyDictionary<string, CoordinateOption[]>? mapMonsters) ? mapMonsters.Keys.ToArray() : [];
	}

	public static CoordinateOption[] GetCoordinates(string map, string monster) {
		return GetLocations().TryGetValue(map, out IReadOnlyDictionary<string, CoordinateOption[]>? mapMonsters) && mapMonsters.TryGetValue(monster, out CoordinateOption[]? values) ? values : [];
	}

	private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, CoordinateOption[]>> GetLocations() {
		if (locations != null) return locations;

		Dictionary<string, Dictionary<string, List<CoordinateOption>>> parsed = new();
		string path = Path.Combine(AppContext.BaseDirectory, "Data", "ToaDo", "ToaDoQuai.map");
		string currentMap = "";

		if (File.Exists(path)) {
			foreach (string sourceLine in File.ReadLines(path, Encoding.Unicode)) {
				string line = sourceLine.Trim();
				if (line.StartsWith('[') && line.EndsWith(']')) {
					currentMap = line[1..^1].Trim();
					if (!AllowedRouteMaps.Contains(currentMap)) currentMap = "";
					else if (!parsed.ContainsKey(currentMap)) parsed[currentMap] = new Dictionary<string, List<CoordinateOption>>();
					continue;
				}
				if (string.IsNullOrWhiteSpace(currentMap)) continue;
				int equalsIndex = line.IndexOf('=');
				if (equalsIndex <= 0) continue;
				string monster = NormalizeMonster(line[..equalsIndex].Trim());
				foreach (string coordinate in line[(equalsIndex + 1)..].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
					string[] parts = coordinate.Split(',', StringSplitOptions.TrimEntries);
					if (parts.Length != 2 || !int.TryParse(parts[0], out int rawX) || !int.TryParse(parts[1], out int rawY)) continue;
					if (!parsed[currentMap].TryGetValue(monster, out List<CoordinateOption>? options)) parsed[currentMap][monster] = options = [];
					options.Add(new CoordinateOption(rawX, rawY));
				}
			}
		}

		locations = parsed.ToDictionary(map => map.Key, map => (IReadOnlyDictionary<string, CoordinateOption[]>)map.Value.ToDictionary(monster => monster.Key, monster => monster.Value.ToArray()));
		return locations;
	}

	private static string NormalizeMonster(string monster) => monster is "Lôi Trạch thần" or "Lôi Thạch thần" ? "Lôi Trạch Thần" : monster;
}
