namespace Auto.Utils;

using System.IO;

public static class GameMapRoutePlanner {
	public static IReadOnlyList<GameMapTransition> FindRoute(int sourceMapId, int destinationMapId) {
		if (sourceMapId <= 0 || destinationMapId <= 0) return Array.Empty<GameMapTransition>();
		if (sourceMapId == destinationMapId) return Array.Empty<GameMapTransition>();
		Dictionary<int, List<GameMapTransition>> graph = LoadGraph();
		Queue<(int MapId, List<GameMapTransition> Route)> queue = new();
		HashSet<int> visited = [sourceMapId];
		queue.Enqueue((sourceMapId, new List<GameMapTransition>()));
		while (queue.Count > 0) {
			(int mapId, List<GameMapTransition> route) = queue.Dequeue();
			if (!graph.TryGetValue(mapId, out List<GameMapTransition>? transitions)) continue;
			foreach (GameMapTransition transition in transitions) {
				if (!visited.Add(transition.ToMapId)) continue;
				List<GameMapTransition> nextRoute = new(route) { transition };
				if (transition.ToMapId == destinationMapId) return nextRoute;
				queue.Enqueue((transition.ToMapId, nextRoute));
			}
		}
		return Array.Empty<GameMapTransition>();
	}

	private static Dictionary<int, List<GameMapTransition>> LoadGraph() {
		Dictionary<int, List<GameMapTransition>> graph = new();
		string mapsPath = Path.Combine(AppContext.BaseDirectory, "Data", "Maps");
		if (!Directory.Exists(mapsPath)) return graph;
		foreach (string mapPath in Directory.EnumerateFiles(mapsPath, "*.map")) {
			if (!int.TryParse(Path.GetFileNameWithoutExtension(mapPath), out int fromMapId)) continue;
			string[] lines = File.ReadAllLines(mapPath);
			foreach (string line in lines) {
				int equalsIndex = line.IndexOf('=');
				if (equalsIndex <= 0 || !int.TryParse(line[..equalsIndex], out int toMapId)) continue;
				string[] values = line[(equalsIndex + 1)..].Split(',', StringSplitOptions.TrimEntries);
				if (values.Length < 4 || values.Length % 2 != 0 || !values.All(value => int.TryParse(value, out _))) continue;
				List<GameMapPoint> points = new();
				for (int valueIndex = 0; valueIndex < values.Length; valueIndex += 2) points.Add(new GameMapPoint(int.Parse(values[valueIndex]), int.Parse(values[valueIndex + 1])));
				GameMapTransition transition = new(fromMapId, toMapId, points);
				if (!graph.TryGetValue(fromMapId, out List<GameMapTransition>? transitions)) graph[fromMapId] = transitions = new List<GameMapTransition>();
				transitions.Add(transition);
			}
		}
		return graph;
	}

}

public sealed record GameMapTransition(int FromMapId, int ToMapId, IReadOnlyList<GameMapPoint> Points) {
	public GameMapPoint Approach => Points[^2];
	public GameMapPoint Far => Points[^1];
}

public readonly record struct GameMapPoint(int RawX, int RawY);
