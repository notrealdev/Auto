namespace Auto.Market;

public sealed class Settings {
	public bool AutoAdvertise { get; set; }

	public string AdvertiseText { get; set; } = "";

	public bool NearbyEnabled { get; set; }

	public int NearbyDelaySeconds { get; set; } = 10;

	public bool AreaEnabled { get; set; }

	public int AreaDelaySeconds { get; set; } = 30;

	public bool TradeEnabled { get; set; }

	public int TradeDelaySeconds { get; set; } = 180;

	public bool TerritoryEnabled { get; set; }

	public int TerritoryDelaySeconds { get; set; } = 30;
}
